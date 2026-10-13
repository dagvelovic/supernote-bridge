using SupernoteSharp.Business;
using SupernoteSharp.Common;

namespace SupernoteBridge.Core;

public record ExportStatus(
    string Phase,           // idle | scanning | exporting | done | error | cancelled
    int TotalFiles,
    int DoneFiles,
    int FailedFiles,
    string? CurrentPath,
    string? TargetDir,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Bulk-exports every .note under a device folder to PDF (vector by default),
/// preserving the folder structure below the chosen target directory.
/// Notes are processed one at a time entirely in RAM; only the resulting PDFs
/// are written (explicit user action, sanctioned by the security policy).
/// </summary>
public sealed class ExportService
{
    private readonly IDeviceTransport _transport;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _running;

    private string _phase = "idle";
    private int _total, _done, _failed;
    private string? _current, _target, _error;
    private List<string> _warnings = new();

    public ExportService(IDeviceTransport transport) => _transport = transport;

    public ExportStatus GetStatus()
    {
        lock (_sync)
            return new ExportStatus(_phase, _total, _done, _failed, _current, _target, _error, _warnings.ToList());
    }

    public bool Start(string deviceFolder, string targetDir, bool vector)
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return false;
            _cts = new CancellationTokenSource();
            _phase = "scanning";
            _total = _done = _failed = 0;
            _current = null; _target = targetDir; _error = null; _warnings = new();
            var token = _cts.Token;
            _running = Task.Run(() => RunAsync(deviceFolder, targetDir, vector, token));
            return true;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync(string deviceFolder, string targetDir, bool vector, CancellationToken ct)
    {
        try
        {
            var notes = new List<DeviceEntry>();
            await WalkAsync(deviceFolder, notes, ct);
            lock (_sync) { _total = notes.Count; _phase = "exporting"; }

            string prefix = deviceFolder.TrimEnd('\\');
            foreach (var f in notes)
            {
                ct.ThrowIfCancellationRequested();
                string rel = f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? f.Path[prefix.Length..].TrimStart('\\')
                    : Path.GetFileName(f.Path);
                lock (_sync) _current = rel;

                try
                {
                    using var ms = new MemoryStream();
                    await _transport.DownloadAsync(f.Path, ms, ct);
                    ms.Position = 0;
                    var parser = new Parser();
                    var notebook = parser.LoadNotebook(ms, Policy.Loose);
                    var converter = new Converter.PdfConverter(notebook, DefaultColorPalette.Grayscale);
                    byte[] pdf = converter.ConvertAll(vectorize: vector, enableLinks: true);

                    string outPath = Path.Combine(targetDir, Path.ChangeExtension(rel, ".pdf"));
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    await File.WriteAllBytesAsync(outPath, pdf, ct);
                    lock (_sync) _done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (_sync) { _failed++; _done++; _warnings.Add($"'{rel}': {ex.Message}"); }
                }
            }

            lock (_sync) { _phase = "done"; _current = null; }
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

    private async Task WalkAsync(string path, List<DeviceEntry> notes, CancellationToken ct)
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
            if (e.IsDirectory) await WalkAsync(e.Path, notes, ct);
            else if (e.Name.EndsWith(".note", StringComparison.OrdinalIgnoreCase)) notes.Add(e);
        }
    }
}
