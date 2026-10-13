using System.Text.Json;

namespace SupernoteBridge.Core;

public record BackupInfo(string Root, string? Serial, string? CreatedAt, int? Files);

/// <summary>
/// Read-only transport over a backup folder on disk (offline mode). A backup folder mirrors
/// device paths 1:1 (`\Internal shared storage\Note\x.note` → `&lt;root&gt;\Internal shared storage\Note\x.note`),
/// so every viewer/export service works on it unchanged. Backups are integrity copies:
/// every write operation is refused.
/// </summary>
public sealed class BackupFolderTransport : IDeviceTransport
{
    public const string ManifestName = "manifest.json";
    private const string TempSuffix = ".sb-tmp"; // leftover of an interrupted backup run

    private readonly string _root;
    private readonly Dictionary<string, ManifestEntry> _manifest = new(StringComparer.OrdinalIgnoreCase);

    public BackupInfo Backup { get; }
    public bool IsConnected => Directory.Exists(_root);
    public DeviceInfo? Info { get; }

    // a folder never attaches or detaches
    public event EventHandler? Attached { add { } remove { } }
    public event EventHandler? Detached { add { } remove { } }

    /// <param name="path">The backup folder, or its manifest.json.</param>
    public BackupFolderTransport(string path)
    {
        string root = Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(path))!
            : Path.GetFullPath(path);
        _root = Path.TrimEndingDirectorySeparator(root);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);

        BackupManifest? manifest = null;
        string manifestPath = Path.Combine(_root, ManifestName);
        if (File.Exists(manifestPath))
        {
            try { manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath)); }
            catch (JsonException) { /* damaged manifest: still browsable, just no metadata */ }
        }
        foreach (var e in manifest?.Files ?? new List<ManifestEntry>()) _manifest[e.Path] = e;
        Backup = new BackupInfo(_root, manifest?.Serial, manifest?.CreatedAt, manifest?.Files?.Count);
        Info = new DeviceInfo("Backup", manifest?.Serial ?? "", Path.GetFileName(_root));
    }

    /// <summary>The manifest record of a file (device-style path), or null if the manifest doesn't list it.</summary>
    public ManifestEntry? ManifestFor(string devicePath) =>
        _manifest.GetValueOrDefault((devicePath ?? "").TrimStart('\\'));

    /// <summary>Maps a device-style path to a full path under the root; rejects anything escaping it.</summary>
    private string Resolve(string devicePath)
    {
        string rel = (devicePath ?? "").Replace('/', '\\').TrimStart('\\');
        string full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path outside the backup folder.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private string ToDevicePath(string full) =>
        Path.GetRelativePath(_root, full) is var rel && rel == "." ? "\\" : "\\" + rel;

    private bool Hidden(FileSystemInfo info) =>
        info is FileInfo && (info.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
            || (info.Name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(info.FullName)!.Equals(_root, StringComparison.OrdinalIgnoreCase)));

    private DeviceEntry ToEntry(FileSystemInfo info) => info is FileInfo f
        ? new DeviceEntry(ToDevicePath(f.FullName), f.Name, false, f.Length, f.LastWriteTime)
        : new DeviceEntry(ToDevicePath(info.FullName), info.Name, true, null, info.LastWriteTime);

    public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var dir = new DirectoryInfo(Resolve(path));
        if (!dir.Exists) throw new DirectoryNotFoundException(path);
        IReadOnlyList<DeviceEntry> result = dir.EnumerateFileSystemInfos()
            .Where(i => !Hidden(i))
            .Select(ToEntry)
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task DownloadAsync(string devicePath, Stream target, CancellationToken ct = default)
    {
        await using var fs = new FileStream(Resolve(devicePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, useAsync: true);
        await fs.CopyToAsync(target, ct);
    }

    public Task<DeviceEntry?> StatAsync(string devicePath, CancellationToken ct = default)
    {
        string full = Resolve(devicePath);
        DeviceEntry? e = null;
        if (File.Exists(full)) { var f = new FileInfo(full); if (!Hidden(f)) e = ToEntry(f); }
        else if (Directory.Exists(full)) e = ToEntry(new DirectoryInfo(full));
        return Task.FromResult(e);
    }

    public Task UploadAsync(Stream source, long length, string devicePath, CancellationToken ct = default) =>
        throw new ReadOnlySourceException();
    public Task DeleteAsync(string devicePath, bool recursive = false, CancellationToken ct = default) =>
        throw new ReadOnlySourceException();
    public Task RenameAsync(string devicePath, string newName, CancellationToken ct = default) =>
        throw new ReadOnlySourceException();
    public Task CreateDirectoryAsync(string devicePath, CancellationToken ct = default) =>
        throw new ReadOnlySourceException();
}

public sealed class ReadOnlySourceException : Exception
{
    public ReadOnlySourceException() : base("A backup is open: it is read-only.") { }
}
