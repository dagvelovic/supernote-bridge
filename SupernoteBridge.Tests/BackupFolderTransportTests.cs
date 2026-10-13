using System.Text.Json;
using SupernoteBridge.Core;

namespace SupernoteBridge.Tests;

/// <summary>Synthetic backup tree in %TEMP% — no device content involved.</summary>
public sealed class BackupFolderTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-test-" + Guid.NewGuid().ToString("N"), "20260101-120000_SUPERNOTE_BACKUP");

    public BackupFolderTransportTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Internal shared storage", "Note", "Empty"));
        File.WriteAllText(Path.Combine(_root, "Internal shared storage", "Note", "b.note"), "BBBB");
        File.WriteAllText(Path.Combine(_root, "Internal shared storage", "Note", "a.pdf"), "AA");
        File.WriteAllText(Path.Combine(_root, "Internal shared storage", "Note", "partial.note.sb-tmp"), "x");
        File.WriteAllText(Path.Combine(_root, BackupFolderTransport.ManifestName), JsonSerializer.Serialize(
            new BackupManifest("SERIAL123", "2026-01-01T12:00:00+01:00",
                [new ManifestEntry("Internal shared storage\\Note\\b.note", 4, null, "00")])));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt"), "secret");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    [Fact]
    public void Opens_from_manifest_or_folder_and_reads_metadata()
    {
        foreach (var path in new[] { _root, _root + "\\", Path.Combine(_root, "manifest.json") })
        {
            var t = new BackupFolderTransport(path);
            Assert.Equal(_root, t.Backup.Root);
            Assert.Equal("SERIAL123", t.Backup.Serial);
            Assert.Equal(1, t.Backup.Files);
            Assert.True(t.IsConnected);
        }
    }

    [Fact]
    public async Task Root_listing_hides_manifest()
    {
        var t = new BackupFolderTransport(_root);
        var root = await t.ListAsync("\\");
        var e = Assert.Single(root);
        Assert.Equal("\\Internal shared storage", e.Path);
        Assert.True(e.IsDirectory);
        Assert.Null(await t.StatAsync("\\manifest.json"));
    }

    [Fact]
    public async Task Listing_uses_device_paths_dirs_first_and_skips_temp_files()
    {
        var t = new BackupFolderTransport(_root);
        var list = await t.ListAsync("\\Internal shared storage\\Note");
        Assert.Equal(new[] { "Empty", "a.pdf", "b.note" }, list.Select(e => e.Name));
        var b = list.Single(e => e.Name == "b.note");
        Assert.Equal("\\Internal shared storage\\Note\\b.note", b.Path);
        Assert.Equal(4, b.Size);
        Assert.False(b.IsDirectory);
    }

    [Fact]
    public async Task Stat_and_download_read_the_file()
    {
        var t = new BackupFolderTransport(_root);
        var st = await t.StatAsync("\\Internal shared storage\\Note\\a.pdf");
        Assert.NotNull(st);
        Assert.Equal(2, st!.Size);
        Assert.Null(await t.StatAsync("\\Internal shared storage\\nope.note"));
        Assert.Equal("\\", (await t.StatAsync("\\"))!.Path);

        using var ms = new MemoryStream();
        await t.DownloadAsync("\\Internal shared storage\\Note\\b.note", ms);
        Assert.Equal("BBBB", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Theory]
    [InlineData("\\..\\outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("\\Internal shared storage\\..\\..\\outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task Paths_cannot_escape_the_backup(string path)
    {
        var t = new BackupFolderTransport(_root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => t.StatAsync(path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => t.DownloadAsync(path, Stream.Null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => t.ListAsync(path));
    }

    [Fact]
    public async Task Write_operations_are_refused_and_change_nothing()
    {
        var t = new BackupFolderTransport(_root);
        const string f = "\\Internal shared storage\\Note\\b.note";
        await Assert.ThrowsAsync<ReadOnlySourceException>(() => t.UploadAsync(new MemoryStream([1]), 1, f));
        await Assert.ThrowsAsync<ReadOnlySourceException>(() => t.DeleteAsync(f));
        await Assert.ThrowsAsync<ReadOnlySourceException>(() => t.RenameAsync(f, "c.note"));
        await Assert.ThrowsAsync<ReadOnlySourceException>(() => t.CreateDirectoryAsync("\\Internal shared storage\\X"));
        Assert.Equal("BBBB", File.ReadAllText(Path.Combine(_root, "Internal shared storage", "Note", "b.note")));
    }

    [Fact]
    public void Missing_folder_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new BackupFolderTransport(Path.Combine(_root, "nope")));
    }

    [Fact]
    public async Task SourceRouter_routes_to_backup_and_back()
    {
        var device = new FakeDevice();
        var sw = new SourceRouter(device);
        int changes = 0, attaches = 0;
        sw.SourceChanged += (_, _) => changes++;
        sw.Attached += (_, _) => attaches++;

        Assert.Null(sw.Backup);
        Assert.Equal("\\device-file", (await sw.ListAsync("\\")).Single().Path);

        sw.UseBackup(_root);
        Assert.Equal(_root, sw.Backup!.Root);
        Assert.Equal("\\Internal shared storage", (await sw.ListAsync("\\")).Single().Path);
        await Assert.ThrowsAsync<ReadOnlySourceException>(() => sw.DeleteAsync("\\x"));
        device.RaiseAttached();               // hot-plug while a backup is open is not forwarded
        Assert.Equal(0, attaches);

        sw.UseDevice();
        Assert.Null(sw.Backup);
        Assert.Equal("\\device-file", (await sw.ListAsync("\\")).Single().Path);
        device.RaiseAttached();
        Assert.Equal(1, attaches);
        Assert.Equal(2, changes);

        Assert.Throws<DirectoryNotFoundException>(() => sw.UseBackup(Path.Combine(_root, "nope")));
        Assert.Null(sw.Backup);               // a failed open keeps the current source
        Assert.Equal(2, changes);
    }

    private sealed class FakeDevice : IDeviceTransport
    {
        public bool IsConnected => true;
        public DeviceInfo? Info => new("Fake", "S", null);
        public event EventHandler? Attached;
        public event EventHandler? Detached { add { } remove { } }
        public void RaiseAttached() => Attached?.Invoke(this, EventArgs.Empty);
        public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeviceEntry>>([new DeviceEntry("\\device-file", "device-file", false, 1, null)]);
        public Task DownloadAsync(string p, Stream t, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadAsync(Stream s, long l, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string p, bool r = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string p, string n, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeviceEntry?> StatAsync(string p, CancellationToken ct = default) => Task.FromResult<DeviceEntry?>(null);
    }
}
