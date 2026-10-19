using System.IO;
using System.Text.Json;
using NativeWidget.Models;

namespace NativeWidget.Services;

/// <summary>Maintains a cached, bounded directory-name index for fast interactive filtering.</summary>
internal sealed class FolderSearchService
{
    internal sealed record FolderEntry(string Name, string Path);
    private sealed class IndexData
    {
        public int SchemaVersion { get; set; }
        public List<string> Roots { get; set; } = new();
        public List<FolderEntry> Folders { get; set; } = new();
        public DateTime UpdatedUtc { get; set; }
        public bool Truncated { get; set; }
    }

    private const int MaxFolders = 100_000;
    private const int CurrentSchemaVersion = 2;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", ".git", ".svn", ".hg", "node_modules", "bower_components", "packages",
        "bin", "obj", ".vs", ".idea", "$RECYCLE.BIN", "System Volume Information",
        ".venv", "venv", "env", ".cache", "__pycache__", ".tox", ".mypy_cache", ".pytest_cache",
        ".ruff_cache", ".next", ".nuxt", ".turbo", ".gradle", ".npm", ".yarn", "coverage", "target"
    };
    private readonly string _path = AppConfig.TokenPath("folder-search.json");
    private readonly int _maxFolders;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IndexData _data = new();
    private int _rootsVersion;

    internal FolderSearchService(int maxFolders = MaxFolders)
    {
        if (maxFolders < 1) throw new ArgumentOutOfRangeException(nameof(maxFolders));
        _maxFolders = maxFolders;
    }

    public IReadOnlyList<string> Roots => _data.Roots;
    public IReadOnlyList<FolderEntry> Folders => _data.Folders;
    public DateTime UpdatedUtc => _data.UpdatedUtc;
    public bool Truncated => _data.Truncated;
    public bool HasIndex => _data.Folders.Count > 0 && _data.UpdatedUtc != default;

    internal static int RankMatch(string name, string path, string query)
    {
        if (string.IsNullOrEmpty(query)) return 2;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    public async Task LoadAsync()
    {
        _data = await Task.Run(() =>
        {
            try { return JsonSerializer.Deserialize<IndexData>(File.ReadAllText(_path)) ?? DefaultData(); }
            catch { return DefaultData(); }
        });

        _data.Roots ??= new();
        _data.Folders ??= new();
        var roots = _data.Roots.Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _data.Roots = PruneNestedRoots(roots);
        _data.Folders = _data.Folders.Where(folder => folder is not null &&
                !string.IsNullOrWhiteSpace(folder.Name) && Path.IsPathFullyQualified(folder.Path))
            .ToList();
        if (_data.SchemaVersion != CurrentSchemaVersion)
        {
            _data.Folders.Clear();
            _data.UpdatedUtc = default;
            _data.Truncated = false;
        }
    }

    private static IndexData DefaultData() => new() { Roots = DefaultRoots() };

    private static List<string> DefaultRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return PruneNestedRoots(roots.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task SetRootsAsync(IEnumerable<string> roots)
    {
        var normalized = roots.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(Path.GetFullPath).Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        normalized = PruneNestedRoots(normalized);
        await _mutationGate.WaitAsync();
        try
        {
            _rootsVersion++;
            _data.Roots = normalized;
            _data.Folders = _data.Folders
                .Where(folder => normalized.Any(root => IsWithinRoot(folder.Path, root)))
                .ToList();
            await SaveAsync(_data);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        var rootsVersion = _rootsVersion;
        var roots = _data.Roots.ToList();
        var next = await Task.Run(() =>
        {
            var folders = new List<FolderEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontiers = roots.Select(_ => new Queue<string>()).ToArray();
            for (var index = 0; index < roots.Count; index++) frontiers[index].Enqueue(roots[index]);
            bool truncated = false;
            while (frontiers.Any(frontier => frontier.Count > 0))
            {
                var nextFrontiers = roots.Select(_ => new Queue<string>()).ToArray();
                while (frontiers.Any(frontier => frontier.Count > 0))
                {
                    for (var rootIndex = 0; rootIndex < frontiers.Length; rootIndex++)
                    {
                        // Process one directory from each root at a time, then advance a level.
                        // This keeps a wide/deep first root from starving other configured roots.
                        if (!frontiers[rootIndex].TryDequeue(out var path)) continue;
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            path = Path.GetFullPath(path);
                            if (!seen.Add(path) || !Directory.Exists(path)) continue;
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var trimmedPath = Path.TrimEndingDirectorySeparator(path);
                            var name = Path.GetFileName(trimmedPath);
                            folders.Add(new FolderEntry(string.IsNullOrEmpty(name) ? path : name, path));
                            if (folders.Count >= _maxFolders)
                            {
                                truncated = true;
                                break;
                            }

                            foreach (var child in Directory.EnumerateDirectories(path))
                            {
                                token.ThrowIfCancellationRequested();
                                if (Excluded.Contains(Path.GetFileName(child))) continue;
                                // Bound queued work while preserving shallow-first order.
                                if (nextFrontiers[rootIndex].Count + folders.Count >= _maxFolders)
                                {
                                    truncated = true;
                                    continue;
                                }
                                // Check attributes before queueing so junctions and symlinks are never traversed.
                                try
                                {
                                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                        nextFrontiers[rootIndex].Enqueue(child);
                                }
                                catch (Exception ex) when (IsExpectedFilesystemError(ex)) { }
                            }
                        }
                        catch (Exception ex) when (IsExpectedFilesystemError(ex)) { }
                    }
                    if (folders.Count >= _maxFolders) break;
                }
                if (truncated && folders.Count >= _maxFolders) break;
                frontiers = nextFrontiers;
            }
            return new IndexData { SchemaVersion = CurrentSchemaVersion, Roots = roots, Folders = folders, UpdatedUtc = DateTime.UtcNow, Truncated = truncated };
        }, token);

        token.ThrowIfCancellationRequested();
        await _mutationGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (rootsVersion != _rootsVersion)
                throw new OperationCanceledException("Folder roots changed during indexing.", token);
            await SaveAsync(next, token);
            token.ThrowIfCancellationRequested();
            _data = next;
        }
        finally { _mutationGate.Release(); }
    }

    private static List<string> PruneNestedRoots(List<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.OrderBy(path => path.Length))
            if (!result.Any(parent => IsWithinRoot(root, parent))) result.Add(root);
        return result;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative.Equals(".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsExpectedFilesystemError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;

    private Task SaveAsync(IndexData data, CancellationToken token = default) => Task.Run(() =>
    {
        AppConfig.EnsureFolder();
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            data.SchemaVersion = CurrentSchemaVersion;
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            token.ThrowIfCancellationRequested();
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }, token);
}
