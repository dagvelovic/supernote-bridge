using System.Security.Cryptography;

namespace SupernoteBridge.Core;

/// <param name="Status">new (missing on device) | same (identical, skipped) | different (device has another version) | damaged (backup copy fails its manifest SHA-256) | blocked (a folder of that name is on the device)</param>
/// <param name="Result">null until restored: restored | skipped: … | failed: …</param>
public record RestoreItem(string Path, string Status, long Size, long? DeviceSize, DateTime? DeviceModified,
    DateTime? BackupModified, string? Result);

public record RestoreStatus(
    string Phase,           // idle | planning | ready | restoring | done | error | cancelled
    string? Source,         // device path chosen in the backup
    string? BackupRoot,
    int Total,
    int Done,
    long TotalBytes,
    long Bytes,
    string? CurrentPath,
    string? Error,
    string? Warning,
    int New, int Same, int Different, int Damaged, int Blocked, int DirsToCreate,
    bool? Overwrite,
    IReadOnlyList<RestoreItem> Items);

/// <summary>
/// Copies a file or folder from an open backup back to its original path on the device.
/// Two steps: Plan (compare every file with the device, nothing written) → the user confirms,
/// choosing overwrite or skip for files the device has in another version → Restore.
/// Each file goes to a temp name first, is read back and checked against its SHA-256, and only
/// then replaces the device file — a failed or corrupt transfer never costs the device copy.
/// The backup is only read. Device content is hashed in RAM (never written to disk).
/// </summary>
public sealed class RestoreService
{
    public const string TempSuffix = ".sb-restore";

    private sealed class Item
    {
        public required string Path;
        public required string Status;
        public required long Size;
        public required string Sha;
        public long? DeviceSize;
        public DateTime? DeviceModified;
        public DateTime? BackupModified;
        public string? Result;
        public RestoreItem ToRecord() => new(Path, Status, Size, DeviceSize, DeviceModified, BackupModified, Result);
    }

    private readonly IDeviceTransport _device;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _running;

    private string _phase = "idle";
    private string? _source, _root, _current, _error, _warning;
    private int _done;
    private long _bytes;
    private bool? _overwrite;
    private List<Item> _items = new();
    private List<string> _dirsToCreate = new();
    private BackupFolderTransport? _backup;

    /// <param name="device">The device itself (never the source router): restore always writes to the device.</param>
    public RestoreService(IDeviceTransport device) => _device = device;

    public RestoreStatus GetStatus()
    {
        lock (_sync)
        {
            int Count(string s) => _items.Count(i => i.Status == s);
            return new RestoreStatus(_phase, _source, _root, _items.Count, _done,
                _items.Sum(i => i.Size), _bytes, _current, _error, _warning,
                Count("new"), Count("same"), Count("different"), Count("damaged"), Count("blocked"), _dirsToCreate.Count,
                _overwrite, _items.Select(i => i.ToRecord()).ToList());
        }
    }

    private bool Busy => _running is { IsCompleted: false };

    /// <summary>Step 1: compare the backup file/folder with the device. Writes nothing.</summary>
    public bool Plan(BackupFolderTransport backup, string path)
    {
        lock (_sync)
        {
            if (Busy) return false;
            _cts = new CancellationTokenSource();
            _phase = "planning";
            _backup = backup; _source = path; _root = backup.Backup.Root;
            _current = _error = _warning = null; _done = 0; _bytes = 0; _overwrite = null;
            _items = new(); _dirsToCreate = new();
            var token = _cts.Token;
            _running = Task.Run(() => PlanAsync(backup, path, token));
            return true;
        }
    }

    /// <summary>Step 2: restore the planned files. overwrite = also replace files the device has in another version.</summary>
    public bool Restore(bool overwrite)
    {
        lock (_sync)
        {
            if (Busy || _phase != "ready" || _backup is null) return false;
            _cts = new CancellationTokenSource();
            _phase = "restoring";
            _overwrite = overwrite; _done = 0; _bytes = 0;
            var backup = _backup;
            var token = _cts.Token;
            _running = Task.Run(() => RestoreAsync(backup, overwrite, token));
            return true;
        }
    }

    /// <summary>Cancels planning/restoring, or discards a plan that is waiting for confirmation.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (Busy) { _cts?.Cancel(); return; }
            if (_phase == "ready") { _phase = "cancelled"; _backup = null; }
        }
    }

    private async Task PlanAsync(BackupFolderTransport backup, string path, CancellationToken ct)
    {
        try
        {
            var root = await backup.StatAsync(path, ct) ?? throw new FileNotFoundException("Not in the backup.", path);
            var files = new List<DeviceEntry>();
            var dirs = new List<string>();
            if (root.IsDirectory) { dirs.Add(root.Path); await WalkAsync(backup, root.Path, files, dirs, ct); }
            else
            {
                files.Add(root);
                // a PDF's handwriting lives in its .pdf.mark sibling — restore them together
                if (root.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    && await backup.StatAsync(root.Path + ".mark", ct) is { IsDirectory: false } mark)
                    files.Add(mark);
            }

            string? backupSerial = backup.Backup.Serial, deviceSerial = _device.Info?.Serial;
            if (!string.IsNullOrEmpty(backupSerial) && !string.IsNullOrEmpty(deviceSerial) && backupSerial != deviceSerial)
                lock (_sync) _warning = $"This backup was made from another device (serial {backupSerial[..Math.Min(8, backupSerial.Length)]}…).";

            var missingDirs = new List<string>();
            foreach (var d in dirs)
                if (await _device.StatAsync(d, ct) is null) missingDirs.Add(d);
            lock (_sync) _dirsToCreate = missingDirs;

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync) _current = f.Path;

                string sha = await HashAsync(backup, f.Path, ct);
                var manifest = backup.ManifestFor(f.Path);
                var dev = await _device.StatAsync(f.Path, ct);
                string status =
                    manifest is not null && manifest.Sha256 != sha ? "damaged"
                    : dev is null ? "new"
                    : dev.IsDirectory ? "blocked"          // a folder of that name is in the way
                    : dev.Size != f.Size ? "different"
                    : await HashAsync(_device, f.Path, ct) == sha ? "same"
                    : "different";
                lock (_sync)
                {
                    _items.Add(new Item
                    {
                        Path = f.Path, Status = status, Size = f.Size ?? 0, Sha = sha,
                        DeviceSize = dev is { IsDirectory: false } ? dev.Size : null,
                        DeviceModified = dev?.Modified, BackupModified = f.Modified,
                    });
                    _done++; _bytes += f.Size ?? 0;
                }
            }
            lock (_sync) { _phase = "ready"; _current = null; }
        }
        catch (OperationCanceledException) { lock (_sync) { _phase = "cancelled"; _current = null; } }
        catch (Exception ex) { lock (_sync) { _phase = "error"; _error = ex.Message; _current = null; } }
    }

    private async Task RestoreAsync(BackupFolderTransport backup, bool overwrite, CancellationToken ct)
    {
        try
        {
            List<string> dirs; List<Item> items;
            lock (_sync) { dirs = _dirsToCreate.ToList(); items = _items.ToList(); }
            foreach (var d in dirs) await _device.CreateDirectoryAsync(d, ct);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync) _current = item.Path;
                string result = item.Status switch
                {
                    "same" => "skipped: identical on the device",
                    "damaged" => "skipped: the backup copy fails its manifest checksum (run Verify)",
                    "blocked" => "skipped: a folder with this name exists on the device",
                    "different" when !overwrite => "skipped: kept the device version",
                    _ => await RestoreFileAsync(backup, item, overwrite, ct),
                };
                lock (_sync) { item.Result = result; _done++; _bytes += item.Size; }
            }
            lock (_sync) { _phase = "done"; _current = null; }
        }
        catch (OperationCanceledException) { lock (_sync) { _phase = "cancelled"; _current = null; } }
        catch (Exception ex) { lock (_sync) { _phase = "error"; _error = ex.Message; _current = null; } }
    }

    private async Task<string> RestoreFileAsync(BackupFolderTransport backup, Item item, bool overwrite, CancellationToken ct)
    {
        string tmp = item.Path + TempSuffix;
        try
        {
            using var ms = new MemoryStream(item.Size is > 0 and <= int.MaxValue ? (int)item.Size : 0);
            await backup.DownloadAsync(item.Path, ms, ct);
            if (Sha(ms.ToArray()) != item.Sha) return "failed: the backup file changed since the check";

            int cut = item.Path.LastIndexOf('\\');
            if (cut > 0) await _device.CreateDirectoryAsync(item.Path[..cut], ct);
            if (await _device.StatAsync(tmp, ct) is not null) await _device.DeleteAsync(tmp, false, ct);
            ms.Position = 0;
            await _device.UploadAsync(ms, ms.Length, tmp, ct);

            if (await HashAsync(_device, tmp, ct) != item.Sha)
            {
                await _device.DeleteAsync(tmp, false, ct);
                return "failed: the copy on the device didn't match the backup; the device file is unchanged";
            }

            var dev = await _device.StatAsync(item.Path, ct);
            if (dev is not null)
            {
                if (item.Status == "new" && !overwrite)
                {
                    await _device.DeleteAsync(tmp, false, ct);
                    return "skipped: the file appeared on the device after the check (kept the device version)";
                }
                await _device.DeleteAsync(item.Path, false, ct);
            }
            await _device.RenameAsync(tmp, item.Path[(cut + 1)..], ct);
            return "restored (verified)";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "failed: " + ex.Message; }
    }

    private static async Task WalkAsync(IDeviceTransport t, string path, List<DeviceEntry> files, List<string> dirs, CancellationToken ct)
    {
        foreach (var e in await t.ListAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory) { dirs.Add(e.Path); await WalkAsync(t, e.Path, files, dirs, ct); }
            else files.Add(e);
        }
    }

    /// <summary>SHA-256 of a file read through a transport, streamed — nothing is buffered or written.</summary>
    private static async Task<string> HashAsync(IDeviceTransport t, string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using (var cs = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            await t.DownloadAsync(path, cs, ct);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
