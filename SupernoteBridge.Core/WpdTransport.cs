using MediaDevices;

namespace SupernoteBridge.Core;

/// <summary>
/// USB/MTP transport via Windows Portable Devices (MediaDevices wrapper).
/// All device access is serialized through one gate: WPD COM objects are not thread-safe
/// and the device answers one request at a time anyway.
/// Hot-plug is detected by polling MediaDevice.GetDevices() (no message pump available).
/// </summary>
public sealed class WpdTransport : IDeviceTransport, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _pollTimer;
    private MediaDevice? _device;
    private DeviceInfo? _info;
    private bool _disposed;

    public event EventHandler? Attached;
    public event EventHandler? Detached;

    public bool IsConnected => _device is not null;
    public DeviceInfo? Info => _info;

    public WpdTransport()
    {
        _pollTimer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    private static bool IsSupernote(MediaDevice d)
    {
        return Contains(d.Description, "supernote") || Contains(d.FriendlyName, "supernote")
            || Contains(d.FriendlyName, "nomad") || Contains(d.Manufacturer, "supernote")
            || Contains(d.Manufacturer, "ratta");
        static bool Contains(string? s, string needle) =>
            s?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
    }

    // MediaDevice.GetDevices() returns CACHED SHARED instances — the same object for the
    // same device across calls. Disposing any enumerated instance kills a live connection,
    // so nothing obtained from GetDevices() is ever disposed here (verified empirically).
    private void Poll()
    {
        if (_disposed || !_gate.Wait(0)) return; // skip a beat if an operation is running
        try
        {
            var all = MediaDevice.GetDevices().ToList();
            if (_device is null)
            {
                var found = all.FirstOrDefault(IsSupernote);
                if (found is not null)
                {
                    if (!found.IsConnected) found.Connect();
                    _device = found;
                    _info = new DeviceInfo(found.Model, found.SerialNumber, found.Description);
                    Attached?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (!all.Any(d => d.DeviceId == _device.DeviceId))
            {
                DropDevice();
            }
        }
        catch
        {
            // enumeration hiccups are non-fatal; next poll retries
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DropDevice()
    {
        try { _device?.Disconnect(); } catch { }
        _device = null;
        _info = null;
        Detached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a device operation on the gate; maps a mid-operation unplug to Detached.</summary>
    private async Task<T> RunAsync<T>(Func<MediaDevice, T> op, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var device = _device ?? throw new DeviceNotConnectedException();
            return await Task.Run(() => op(device), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DeviceNotConnectedException && _device is not null && !ProbeAlive())
        {
            DropDevice();
            throw new DeviceNotConnectedException();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ProbeAlive()
    {
        try { return _device is not null && MediaDevice.GetDevices().Any(d => d.DeviceId == _device.DeviceId); }
        catch { return false; }
    }

    public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<DeviceEntry>>(device =>
        {
            var dir = string.IsNullOrEmpty(path) || path == "\\"
                ? device.GetRootDirectory()
                : device.GetDirectoryInfo(path);
            var result = new List<DeviceEntry>();
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                bool isDir = info is MediaDirectoryInfo;
                result.Add(new DeviceEntry(
                    info.FullName,
                    info.Name,
                    isDir,
                    isDir ? null : (long?)info.Length,
                    Normalize(info.LastWriteTime)));
            }
            return result.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);

    public Task DownloadAsync(string devicePath, Stream target, CancellationToken ct = default) =>
        RunAsync<object?>(device => { device.DownloadFile(devicePath, target); return null; }, ct);

    /// <summary>Suffix of the temporary copy used while replacing an existing device file.</summary>
    public const string UploadTempSuffix = ".sb-upload";

    // Replacing an existing file never deletes it before the new copy is safely on the device:
    // upload to a temp name, check the size, then delete the original and rename the temp.
    // If anything fails midway, the original (or the complete temp copy) is still there.
    public Task UploadAsync(Stream source, long length, string devicePath, CancellationToken ct = default) =>
        RunAsync<object?>(device =>
        {
            if (!device.FileExists(devicePath)) { device.UploadFile(source, devicePath); return null; }

            string tmp = devicePath + UploadTempSuffix;
            if (device.FileExists(tmp)) device.DeleteFile(tmp);
            device.UploadFile(source, tmp);
            long written = (long)device.GetFileInfo(tmp).Length;
            if (written != length)
            {
                device.DeleteFile(tmp);
                throw new IOException($"Upload incomplete ({written} of {length} bytes); the original file was kept.");
            }
            device.DeleteFile(devicePath);
            device.Rename(tmp, devicePath[(devicePath.LastIndexOf('\\') + 1)..]);
            return null;
        }, ct);

    public Task CreateDirectoryAsync(string devicePath, CancellationToken ct = default) =>
        RunAsync<object?>(device =>
        {
            string acc = "";
            foreach (var part in devicePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "\\" + part;
                if (!device.DirectoryExists(acc)) device.CreateDirectory(acc);
            }
            return null;
        }, ct);

    public Task DeleteAsync(string devicePath, bool recursive = false, CancellationToken ct = default) =>
        RunAsync<object?>(device =>
        {
            if (device.DirectoryExists(devicePath)) device.DeleteDirectory(devicePath, recursive);
            else device.DeleteFile(devicePath);
            return null;
        }, ct);

    public Task RenameAsync(string devicePath, string newName, CancellationToken ct = default) =>
        RunAsync<object?>(device =>
        {
            if (newName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new ArgumentException("Invalid characters in new name.", nameof(newName));
            device.Rename(devicePath, newName);
            return null;
        }, ct);

    public Task<DeviceEntry?> StatAsync(string devicePath, CancellationToken ct = default) =>
        RunAsync<DeviceEntry?>(device =>
        {
            if (device.DirectoryExists(devicePath))
            {
                var d = device.GetDirectoryInfo(devicePath);
                return new DeviceEntry(d.FullName, d.Name, true, null, Normalize(d.LastWriteTime));
            }
            if (device.FileExists(devicePath))
            {
                var f = device.GetFileInfo(devicePath);
                return new DeviceEntry(f.FullName, f.Name, false, (long?)f.Length, Normalize(f.LastWriteTime));
            }
            return null;
        }, ct);

    private static DateTime? Normalize(DateTime? dt) => dt is { Year: > 1971 } v ? v : null;

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Dispose();
        try { _device?.Disconnect(); } catch { } // shared cached instance — disconnect, never dispose
        _device = null;
        _gate.Dispose();
    }
}

public sealed class DeviceNotConnectedException : Exception
{
    public DeviceNotConnectedException() : base("No Supernote device connected.") { }
}
