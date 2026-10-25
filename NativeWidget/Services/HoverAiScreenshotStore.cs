using System.IO;
using System.Windows.Media.Imaging;

namespace NativeWidget.Services;

internal sealed class HoverAiScreenshotStore
{
    private readonly List<string> _files = new();
    private readonly HashSet<string> _retainedFiles = new();
    internal IReadOnlyList<string> Files => _files.AsReadOnly();
    internal static string DefaultFolder => Path.Combine(Path.GetTempPath(), "NativeWidget", "QuickAI");
    internal static string ResolveFolder(string configured)
    {
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultFolder : configured);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a folder inside the system temp directory.");
        return folder;
    }
    internal string Add(BitmapSource image, string configured)
    {
        var folder = ResolveFolder(configured);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"capture-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        _files.Add(path); return path;
    }
    internal void Clear()
    {
        // Delete only screenshots created by this session, never the selected folder.
        foreach (var path in _files.Concat(_retainedFiles))
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _files.Clear();
        _retainedFiles.Clear();
    }

    internal void Remove(IEnumerable<string> paths, ISet<string> retained)
    {
        foreach (var path in paths)
            if (_files.Remove(path)) _retainedFiles.Add(path);
        foreach (var path in _retainedFiles.Where(path => !retained.Contains(path)).ToArray())
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _retainedFiles.Remove(path);
        }
    }
}
