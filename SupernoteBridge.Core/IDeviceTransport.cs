namespace SupernoteBridge.Core;

public record DeviceEntry(string Path, string Name, bool IsDirectory, long? Size, DateTime? Modified);

public record DeviceInfo(string Model, string Serial, string? Description);

public interface IDeviceTransport
{
    bool IsConnected { get; }
    DeviceInfo? Info { get; }

    Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default);
    Task DownloadAsync(string devicePath, Stream target, CancellationToken ct = default);
    Task UploadAsync(Stream source, long length, string devicePath, CancellationToken ct = default);
    Task DeleteAsync(string devicePath, bool recursive = false, CancellationToken ct = default);
    Task RenameAsync(string devicePath, string newName, CancellationToken ct = default);
    /// <summary>Creates the folder and any missing parents; existing folders are left as they are.</summary>
    Task CreateDirectoryAsync(string devicePath, CancellationToken ct = default);
    Task<DeviceEntry?> StatAsync(string devicePath, CancellationToken ct = default);

    event EventHandler? Attached;
    event EventHandler? Detached;
}
