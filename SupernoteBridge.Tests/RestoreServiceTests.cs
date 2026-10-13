using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupernoteBridge.Core;

namespace SupernoteBridge.Tests;

public sealed class RestoreServiceTests : IDisposable
{
    private const string Dir = "\\Internal shared storage\\Note";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-restore-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryDevice _dev = new("SERIAL1");

    public RestoreServiceTests()
    {
        var entries = new List<ManifestEntry>();
        void Add(string rel, string text, bool listCorrectly = true)
        {
            string full = Path.Combine(_root, rel.TrimStart('\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            entries.Add(new ManifestEntry(rel.TrimStart('\\'), text.Length, null, listCorrectly ? Sha(text) : Sha("other")));
        }
        Add(Dir + "\\new.note", "NEW");
        Add(Dir + "\\same.note", "SAME");
        Add(Dir + "\\diff.note", "BACKUP-VERSION");
        Add(Dir + "\\bad.note", "BAD", listCorrectly: false);
        Add(Dir + "\\blocked.note", "BLK");
        Add(Dir + "\\Sub\\deep.txt", "DEEP");
        Add(Dir + "\\doc.pdf", "PDF");
        Add(Dir + "\\doc.pdf.mark", "MARK");
        Directory.CreateDirectory(Path.Combine(_root, "Internal shared storage", "Note", "EmptyDir"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"),
            JsonSerializer.Serialize(new BackupManifest("SERIAL1", "2026-01-01T00:00:00", entries)));

        _dev.Dirs.Add("\\Internal shared storage");
        _dev.Dirs.Add(Dir);
        _dev.Files[Dir + "\\same.note"] = Bytes("SAME");
        _dev.Files[Dir + "\\diff.note"] = Bytes("DEVICE-VERSION");
        _dev.Dirs.Add(Dir + "\\blocked.note");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Bytes(s))).ToLowerInvariant();
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    private static RestoreStatus WaitFor(RestoreService r, params string[] phases)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RestoreStatus s;
        while (!phases.Contains((s = r.GetStatus()).Phase) && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(10);
        Assert.Contains(s.Phase, phases);
        return s;
    }

    private (RestoreService, RestoreStatus) PlanFolder()
    {
        var r = new RestoreService(_dev);
        Assert.True(r.Plan(new BackupFolderTransport(_root), Dir));
        return (r, WaitFor(r, "ready", "error"));
    }

    private string StatusOf(RestoreStatus s, string name) => s.Items.Single(i => i.Path.EndsWith("\\" + name)).Status;
    private string ResultOf(RestoreStatus s, string name) => s.Items.Single(i => i.Path.EndsWith("\\" + name)).Result!;

    [Fact]
    public void Plan_classifies_every_file_and_writes_nothing()
    {
        var before = _dev.Snapshot();
        var (_, s) = PlanFolder();
        Assert.Equal("ready", s.Phase);
        Assert.Equal("new", StatusOf(s, "new.note"));
        Assert.Equal("same", StatusOf(s, "same.note"));
        Assert.Equal("different", StatusOf(s, "diff.note"));
        Assert.Equal("damaged", StatusOf(s, "bad.note"));
        Assert.Equal("blocked", StatusOf(s, "blocked.note"));
        Assert.Equal((4, 1, 1, 1, 1), (s.New, s.Same, s.Different, s.Damaged, s.Blocked)); // new: new.note, deep.txt, doc.pdf, doc.pdf.mark
        Assert.Equal(2, s.DirsToCreate); // Sub, EmptyDir
        Assert.Null(s.Warning);
        Assert.Equal(before, _dev.Snapshot());
    }

    [Fact]
    public void Restore_with_skip_brings_back_missing_files_and_keeps_device_versions()
    {
        var (r, _) = PlanFolder();
        Assert.True(r.Restore(overwrite: false));
        var s = WaitFor(r, "done", "error");
        Assert.Equal("NEW", Text(_dev.Files[Dir + "\\new.note"]));
        Assert.Equal("DEEP", Text(_dev.Files[Dir + "\\Sub\\deep.txt"]));
        Assert.Equal("DEVICE-VERSION", Text(_dev.Files[Dir + "\\diff.note"]));
        Assert.False(_dev.Files.ContainsKey(Dir + "\\bad.note"));
        Assert.Contains(Dir + "\\EmptyDir", _dev.Dirs);
        Assert.StartsWith("restored", ResultOf(s, "new.note"));
        Assert.StartsWith("skipped: kept", ResultOf(s, "diff.note"));
        Assert.StartsWith("skipped: identical", ResultOf(s, "same.note"));
        Assert.StartsWith("skipped: the backup copy", ResultOf(s, "bad.note"));
        Assert.StartsWith("skipped: a folder", ResultOf(s, "blocked.note"));
        Assert.DoesNotContain(_dev.Files.Keys, k => k.EndsWith(RestoreService.TempSuffix));
    }

    [Fact]
    public void Restore_with_overwrite_replaces_the_different_device_file()
    {
        var (r, _) = PlanFolder();
        Assert.True(r.Restore(overwrite: true));
        var s = WaitFor(r, "done", "error");
        Assert.Equal("BACKUP-VERSION", Text(_dev.Files[Dir + "\\diff.note"]));
        Assert.StartsWith("restored", ResultOf(s, "diff.note"));
    }

    [Fact]
    public void Corrupted_transfer_fails_and_keeps_the_device_file()
    {
        _dev.CorruptUploads = true;
        var (r, _) = PlanFolder();
        Assert.True(r.Restore(overwrite: true));
        var s = WaitFor(r, "done", "error");
        Assert.StartsWith("failed: the copy on the device didn't match", ResultOf(s, "diff.note"));
        Assert.Equal("DEVICE-VERSION", Text(_dev.Files[Dir + "\\diff.note"]));
        Assert.False(_dev.Files.ContainsKey(Dir + "\\new.note"));
        Assert.DoesNotContain(_dev.Files.Keys, k => k.EndsWith(RestoreService.TempSuffix));
    }

    [Fact]
    public void Single_pdf_restore_includes_its_mark()
    {
        var r = new RestoreService(_dev);
        Assert.True(r.Plan(new BackupFolderTransport(_root), Dir + "\\doc.pdf"));
        var s = WaitFor(r, "ready");
        Assert.Equal(new[] { "doc.pdf", "doc.pdf.mark" }, s.Items.Select(i => Path.GetFileName(i.Path.Replace('\\', '/'))));
        Assert.True(r.Restore(false));
        WaitFor(r, "done");
        Assert.Equal("MARK", Text(_dev.Files[Dir + "\\doc.pdf.mark"]));
    }

    [Fact]
    public void Restore_needs_a_ready_plan_and_cancel_discards_it()
    {
        var r = new RestoreService(_dev);
        Assert.False(r.Restore(true));
        Assert.True(r.Plan(new BackupFolderTransport(_root), Dir));
        WaitFor(r, "ready");
        r.Cancel();
        Assert.Equal("cancelled", r.GetStatus().Phase);
        Assert.False(r.Restore(true));
        Assert.False(_dev.Files.ContainsKey(Dir + "\\new.note"));
    }

    [Fact]
    public void Backup_from_another_device_warns()
    {
        var r = new RestoreService(new MemoryDevice("OTHER") { Dirs = { "\\Internal shared storage" } });
        Assert.True(r.Plan(new BackupFolderTransport(_root), Dir));
        Assert.StartsWith("This backup was made from another device", WaitFor(r, "ready").Warning);
    }

    /// <summary>In-memory MTP stand-in: case-insensitive paths, optional upload corruption.</summary>
    private sealed class MemoryDevice(string serial) : IDeviceTransport
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool CorruptUploads { get; set; }
        public bool IsConnected => true;
        public DeviceInfo? Info => new("Mem", serial, null);
        public event EventHandler? Attached { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }

        public string Snapshot() => string.Join("|", Files.OrderBy(f => f.Key).Select(f => f.Key + "=" + Text(f.Value)))
            + "#" + string.Join("|", Dirs.Order());

        public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadAsync(string p, Stream t, CancellationToken ct = default) { t.Write(Files[p]); return Task.CompletedTask; }
        public Task UploadAsync(Stream s, long l, string p, CancellationToken ct = default)
        {
            Assert.True(Dirs.Contains(p[..p.LastIndexOf('\\')]), "parent folder must exist: " + p);
            var ms = new MemoryStream(); s.CopyTo(ms);
            var b = ms.ToArray();
            if (CorruptUploads && b.Length > 0) b[0] ^= 0xFF;
            Files[p] = b;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string p, bool r = false, CancellationToken ct = default) { Files.Remove(p); return Task.CompletedTask; }
        public Task RenameAsync(string p, string n, CancellationToken ct = default)
        {
            var b = Files[p]; Files.Remove(p); Files[p[..(p.LastIndexOf('\\') + 1)] + n] = b; return Task.CompletedTask;
        }
        public Task CreateDirectoryAsync(string p, CancellationToken ct = default)
        {
            string acc = "";
            foreach (var part in p.Split('\\', StringSplitOptions.RemoveEmptyEntries)) Dirs.Add(acc += "\\" + part);
            return Task.CompletedTask;
        }
        public Task<DeviceEntry?> StatAsync(string p, CancellationToken ct = default) => Task.FromResult<DeviceEntry?>(
            Files.TryGetValue(p, out var b) ? new DeviceEntry(p, p[(p.LastIndexOf('\\') + 1)..], false, b.Length, null)
            : Dirs.Contains(p) ? new DeviceEntry(p, p[(p.LastIndexOf('\\') + 1)..], true, null, null)
            : null);
    }
}
