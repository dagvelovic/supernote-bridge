using System.Security.Cryptography;
using System.Text.Json;

namespace SupernoteBridge.Core;

public record ManifestEntry(string Path, long Size, string? Mtime, string Sha256);

public record BackupManifest(string Serial, string CreatedAt, List<ManifestEntry> Files);

public record BackupStatus(
    string Phase,           // idle | scanning | copying | done | error | cancelled
    int TotalFiles,
    int DoneFiles,
    int CopiedFiles,
    long BytesCopied,
    string? CurrentPath,
    string? TargetDir,
    string? LastRun,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Each run creates "{yyyyMMdd-HHmmss}_SUPERNOTE_BACKUP" under the chosen parent folder and
/// downloads the COMPLETE device tree into it (files and empty folders) — every file fresh
/// from the device, no hardlinks, no reuse of earlier backups: every backup folder is a fully
/// independent physical copy (design decision: integrity over time/space). manifest.json
/// (path/size/mtime/sha256) is written into every backup folder. This is the sanctioned
/// on-disk location for device content (see decisions.md).
/// </summary>
public sealed class BackupService
{
    private const string FolderSuffix = "_SUPERNOTE_BACKUP";

    private readonly IDeviceTransport _transport;
    private readonly string _defaultRoot;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _running;

    private string _phase = "idle";
    private int _total, _done, _copied;
    private long _bytes;
    private string? _current, _target, _lastRun, _error;
    private List<string> _warnings = new();

    public BackupService(IDeviceTransport transport, string defaultRoot)
    {
        _transport = transport;
        _defaultRoot = defaultRoot;
    }

    public string DefaultRoot => _defaultRoot;

    public BackupStatus GetStatus()
    {
        lock (_sync)
            return new BackupStatus(_phase, _total, _done, _copied, _bytes,
                _current, _target, _lastRun, _error, _warnings.ToList());
    }

    public bool Start(string? rootOverride = null)
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return false;
            string parent = string.IsNullOrWhiteSpace(rootOverride) ? _defaultRoot : rootOverride;
            _cts = new CancellationTokenSource();
            _phase = "scanning";
            _total = _done = _copied = 0;
            _bytes = 0; _current = null; _target = null; _error = null; _warnings = new();
            var token = _cts.Token;
            _running = Task.Run(() => RunAsync(parent, token));
            return true;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync(string parentRoot, CancellationToken ct)
    {
        try
        {
            var serial = _transport.Info?.Serial ?? throw new DeviceNotConnectedException();
            string destDir = Path.Combine(parentRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + FolderSuffix);
            Directory.CreateDirectory(destDir);
            lock (_sync) _target = destDir;

            // 1. scan the device tree (files AND directories, so empty folders mirror too)
            var files = new List<DeviceEntry>();
            var dirs = new List<string>();
            await WalkAsync("\\", files, dirs, ct);
            lock (_sync) { _total = files.Count; _phase = "copying"; }

            foreach (var d in dirs)
                Directory.CreateDirectory(Path.Combine(destDir, d.TrimStart('\\')));

            // 2. download EVERY file fresh from the device — full independent copy
            var manifest = new List<ManifestEntry>();
            string manifestPath = Path.Combine(destDir, "manifest.json");
            int sinceSave = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                string rel = f.Path.TrimStart('\\');
                string local = Path.Combine(destDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                lock (_sync) _current = rel;

                string? mtime = f.Modified?.ToString("o");
                string tmp = local + ".sb-tmp";
                string sha;
                try
                {
                    using var sha256 = SHA256.Create();
                    await using (var fs = File.Create(tmp))
                    await using (var cs = new CryptoStream(fs, sha256, CryptoStreamMode.Write))
                        await _transport.DownloadAsync(f.Path, cs, ct);
                    sha = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
                    File.Move(tmp, local, overwrite: true);
                    if (f.Modified is DateTime dt) File.SetLastWriteTime(local, dt);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }

                manifest.Add(new ManifestEntry(rel, f.Size ?? new FileInfo(local).Length, mtime, sha));
                lock (_sync) { _copied++; _done++; _bytes += f.Size ?? 0; }

                if (++sinceSave >= 50) { await SaveManifestAsync(manifestPath, serial, manifest, ct); sinceSave = 0; }
            }

            await SaveManifestAsync(manifestPath, serial, manifest, ct);
            lock (_sync) { _phase = "done"; _current = null; _lastRun = DateTime.Now.ToString("o"); }
        }
        catch (OperationCanceledException)
        {
            lock (_sync) { _phase = "cancelled"; _current = null; }
        }
        catch (Exception ex)
        {
            lock (_sync) { _phase = "error"; _error = ex.Message; _current = null; }
        }
    }

    private async Task WalkAsync(string path, List<DeviceEntry> files, List<string> dirs, CancellationToken ct)
    {
        IReadOnlyList<DeviceEntry> entries;
        try
        {
            entries = await _transport.ListAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeviceNotConnectedException)
        {
            lock (_sync) _warnings.Add($"skipped '{path}': {ex.Message}");
            return;
        }
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory) { dirs.Add(e.Path); await WalkAsync(e.Path, files, dirs, ct); }
            else files.Add(e);
        }
    }

    private static async Task SaveManifestAsync(string path, string serial, List<ManifestEntry> files, CancellationToken ct)
    {
        var manifest = new BackupManifest(serial, DateTime.Now.ToString("o"), files);
        string tmp = path + ".sb-tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(tmp, path, overwrite: true);
    }

}
