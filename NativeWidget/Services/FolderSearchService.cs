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
        public List<string> Roots { get; set; } = new();
        public List<FolderEntry> Folders { get; set; } = new();
        public DateTime UpdatedUtc { get; set; }
        public bool Truncated { get; set; }
    }

    private const int MaxFolders = 100_000;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", ".git", ".svn", ".hg", "node_modules", "bower_components", "packages",
        "bin", "obj", ".vs", ".idea", "$RECYCLE.BIN", "System Volume Information"
    };
    private readonly string _path = AppConfig.TokenPath("folder-search.json");
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IndexData _data = new();
    private int _rootsVersion;

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
        _data.Roots = _data.Roots.Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _data.Folders = _data.Folders.Where(folder => folder is not null &&
                !string.IsNullOrWhiteSpace(folder.Name) && Path.IsPathFullyQualified(folder.Path))
            .ToList();
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
        return roots.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SetRootsAsync(IEnumerable<string> roots)
    {
        var normalized = roots.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Select(Path.GetFullPath).Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            var pending = new Stack<string>(roots.AsEnumerable().Reverse());
            bool truncated = false;
            while (pending.TryPop(out var path))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    path = Path.GetFullPath(path);
                    if (!seen.Add(path) || !Directory.Exists(path)) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var name = Path.GetFileName(trimmedPath);
                    folders.Add(new FolderEntry(string.IsNullOrEmpty(name) ? path : name, path));
                    if (folders.Count >= MaxFolders) { truncated = pending.Count > 0; break; }

                    foreach (var child in Directory.EnumerateDirectories(path))
                    {
                        token.ThrowIfCancellationRequested();
                        var childName = Path.GetFileName(child);
                        if (Excluded.Contains(childName)) continue;
                        if (folders.Count + pending.Count >= MaxFolders)
                        {
                            truncated = true;
                            break;
                        }
                        // Check attributes before queueing so junctions and symlinks are never traversed.
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                        }
                        catch (Exception ex) when (IsExpectedFilesystemError(ex)) { }
                    }
                }
                catch (Exception ex) when (IsExpectedFilesystemError(ex)) { }
            }
            return new IndexData { Roots = roots, Folders = folders, UpdatedUtc = DateTime.UtcNow, Truncated = truncated };
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

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedFilesystemError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;

    private Task SaveAsync(IndexData data, CancellationToken token = default) => Task.Run(() =>
    {
        AppConfig.EnsureFolder();
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            token.ThrowIfCancellationRequested();
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }, token);
}
