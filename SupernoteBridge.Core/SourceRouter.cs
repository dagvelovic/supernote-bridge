namespace SupernoteBridge.Core;

/// <summary>
/// The transport every viewer/export service talks to: routes to the live device or to a
/// backup folder opened from disk (offline mode). Backups themselves are always taken from
/// the device transport directly, never through this switch.
/// </summary>
public sealed class SourceRouter : IDeviceTransport
{
    private readonly IDeviceTransport _device;
    private volatile BackupFolderTransport? _backup;

    public SourceRouter(IDeviceTransport device)
    {
        _device = device;
        // device hot-plug only matters while the device is the active source
        _device.Attached += (s, e) => { if (_backup is null) Attached?.Invoke(this, e); };
        _device.Detached += (s, e) => { if (_backup is null) Detached?.Invoke(this, e); };
    }

    public event EventHandler? Attached;
    public event EventHandler? Detached;
    /// <summary>Raised after every switch, so RAM caches of the previous source can be dropped.</summary>
    public event EventHandler? SourceChanged;

    /// <summary>The open backup, or null when the live device is the source.</summary>
    public BackupInfo? Backup => _backup?.Backup;
    /// <summary>The open backup's transport (restore reads from it), or null in device mode.</summary>
    public BackupFolderTransport? BackupSource => _backup;

    private IDeviceTransport Active => (IDeviceTransport?)_backup ?? _device;

    public void UseBackup(string path)
    {
        _backup = new BackupFolderTransport(path); // throws before switching if the folder is gone
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UseDevice()
    {
        _backup = null;
        SourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsConnected => Active.IsConnected;
    public DeviceInfo? Info => Active.Info;

    public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default) => Active.ListAsync(path, ct);
    public Task DownloadAsync(string devicePath, Stream target, CancellationToken ct = default) => Active.DownloadAsync(devicePath, target, ct);
    public Task UploadAsync(Stream source, long length, string devicePath, CancellationToken ct = default) => Active.UploadAsync(source, length, devicePath, ct);
    public Task DeleteAsync(string devicePath, bool recursive = false, CancellationToken ct = default) => Active.DeleteAsync(devicePath, recursive, ct);
    public Task RenameAsync(string devicePath, string newName, CancellationToken ct = default) => Active.RenameAsync(devicePath, newName, ct);
    public Task CreateDirectoryAsync(string devicePath, CancellationToken ct = default) => Active.CreateDirectoryAsync(devicePath, ct);
    public Task<DeviceEntry?> StatAsync(string devicePath, CancellationToken ct = default) => Active.StatAsync(devicePath, ct);
}
