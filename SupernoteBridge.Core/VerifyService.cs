using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SupernoteBridge.Core;

/// <param name="Kind">missing | size | hash | unreadable | extra | temp | manifest</param>
public record VerifyProblem(string Kind, string Path, string? Detail);

public record VerifyStatus(
    string Phase,           // idle | verifying | done | error | cancelled
    string? Root,
    int TotalFiles,
    int DoneFiles,
    int OkFiles,
    long TotalBytes,
    long BytesHashed,
    long ElapsedMs,
    string? CurrentPath,
    string? Error,
    IReadOnlyList<VerifyProblem> Problems);

/// <summary>
/// Re-checks a backup folder against its manifest.json: every listed file must exist with the
/// recorded size and SHA-256; files on disk that the manifest doesn't list (an interrupted
/// backup) and leftover *.sb-tmp files are reported too. Reads only — never writes into the
/// backup (backups are read-only integrity copies).
/// </summary>
public sealed class VerifyService
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _running;

    private string _phase = "idle";
    private string? _root, _current, _error;
    private int _total, _done, _ok;
    private long _totalBytes, _bytes;
    private readonly Stopwatch _clock = new();
    private List<VerifyProblem> _problems = new();

    public VerifyStatus GetStatus()
    {
        lock (_sync)
            return new VerifyStatus(_phase, _root, _total, _done, _ok, _totalBytes, _bytes,
                _clock.ElapsedMilliseconds, _current, _error, _problems.ToList());
    }

    public bool Start(string backupRoot)
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return false;
            _cts = new CancellationTokenSource();
            _phase = "verifying";
            _root = backupRoot; _current = null; _error = null;
            _total = _done = _ok = 0; _totalBytes = _bytes = 0;
            _problems = new();
            _clock.Restart();
            var token = _cts.Token;
            _running = Task.Run(() => RunAsync(backupRoot, token));
            return true;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private void Problem(string kind, string path, string? detail = null)
    {
        lock (_sync) _problems.Add(new VerifyProblem(kind, path, detail));
    }

    private async Task RunAsync(string root, CancellationToken ct)
    {
        try
        {
            string manifestPath = Path.Combine(root, BackupFolderTransport.ManifestName);
            BackupManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Problem("manifest", BackupFolderTransport.ManifestName, ex is FileNotFoundException ? "missing" : ex.Message);
            }
            var files = manifest?.Files ?? new List<ManifestEntry>();
            lock (_sync) { _total = files.Count; _totalBytes = files.Sum(f => f.Size); }

            // 1. every manifest entry: exists, size, SHA-256
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync) _current = f.Path;
                string full = Path.GetFullPath(Path.Combine(root, f.Path));
                try
                {
                    if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException("manifest path points outside the backup folder");
                    var info = new FileInfo(full);
                    if (!info.Exists) Problem("missing", f.Path);
                    else if (info.Length != f.Size) Problem("size", f.Path, $"manifest {f.Size} B, disk {info.Length} B");
                    else
                    {
                        string sha;
                        await using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                        if (sha != f.Sha256) Problem("hash", f.Path, "content differs from the manifest");
                        else lock (_sync) _ok++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Problem("unreadable", f.Path, ex.Message);
                }
                lock (_sync) { _done++; _bytes += f.Size; }
            }

            // 2. files on disk the manifest doesn't know about
            lock (_sync) _current = null;
            var listed = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(root, full);
                if (rel.Equals(BackupFolderTransport.ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.EndsWith(".sb-tmp", StringComparison.OrdinalIgnoreCase))
                    Problem("temp", rel, "leftover of an interrupted backup");
                else if (!listed.Contains(rel))
                    Problem("extra", rel, "not in the manifest (interrupted backup, or added later)");
            }

            lock (_sync) { _phase = "done"; _clock.Stop(); }
        }
        catch (OperationCanceledException)
        {
            lock (_sync) { _phase = "cancelled"; _current = null; _clock.Stop(); }
        }
        catch (Exception ex)
        {
            lock (_sync) { _phase = "error"; _error = ex.Message; _current = null; _clock.Stop(); }
        }
    }
}
