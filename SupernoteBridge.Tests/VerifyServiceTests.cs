using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupernoteBridge.Core;

namespace SupernoteBridge.Tests;

/// <summary>Synthetic backup with a correct manifest, then tampered in specific ways.</summary>
public sealed class VerifyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb-verify-" + Guid.NewGuid().ToString("N"));
    private static readonly string A = Path.Combine("Internal shared storage", "Note", "a.note");
    private static readonly string B = Path.Combine("Internal shared storage", "Note", "b.pdf");

    public VerifyServiceTests()
    {
        var entries = new List<ManifestEntry>();
        foreach (var (rel, text) in new[] { (A, "note content"), (B, "pdf content") })
        {
            string full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            entries.Add(new ManifestEntry(rel, text.Length, null,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()));
        }
        File.WriteAllText(Path.Combine(_root, "manifest.json"),
            JsonSerializer.Serialize(new BackupManifest("S", "2026-01-01T00:00:00", entries)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VerifyStatus Run()
    {
        var v = new VerifyService();
        Assert.True(v.Start(_root));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        VerifyStatus s;
        while ((s = v.GetStatus()).Phase == "verifying" && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(20);
        Assert.Equal("done", s.Phase);
        return s;
    }

    [Fact]
    public void Intact_backup_has_no_problems()
    {
        var s = Run();
        Assert.Empty(s.Problems);
        Assert.Equal(2, s.OkFiles);
        Assert.Equal(2, s.TotalFiles);
        Assert.Equal(s.TotalBytes, s.BytesHashed);
    }

    [Fact]
    public void Changed_byte_same_size_is_a_hash_problem()
    {
        File.WriteAllText(Path.Combine(_root, A), "note contenT");
        var p = Assert.Single(Run().Problems);
        Assert.Equal(("hash", A), (p.Kind, p.Path));
    }

    [Fact]
    public void Truncated_file_is_a_size_problem()
    {
        File.WriteAllText(Path.Combine(_root, B), "pdf");
        var p = Assert.Single(Run().Problems);
        Assert.Equal(("size", B), (p.Kind, p.Path));
    }

    [Fact]
    public void Deleted_file_is_missing()
    {
        File.Delete(Path.Combine(_root, A));
        var s = Run();
        var p = Assert.Single(s.Problems);
        Assert.Equal(("missing", A), (p.Kind, p.Path));
        Assert.Equal(1, s.OkFiles);
    }

    [Fact]
    public void Extra_and_temp_files_are_reported()
    {
        File.WriteAllText(Path.Combine(_root, "Internal shared storage", "new.note"), "x");
        File.WriteAllText(Path.Combine(_root, "Internal shared storage", "Note", "c.note.sb-tmp"), "x");
        var kinds = Run().Problems.Select(p => p.Kind).Order().ToArray();
        Assert.Equal(new[] { "extra", "temp" }, kinds);
    }

    [Fact]
    public void Missing_manifest_is_reported()
    {
        File.Delete(Path.Combine(_root, "manifest.json"));
        var s = Run();
        Assert.Contains(s.Problems, p => p.Kind == "manifest");
        Assert.Equal(2, s.Problems.Count(p => p.Kind == "extra")); // nothing listed → all files unexpected
    }

    [Fact]
    public void Manifest_path_escaping_the_backup_is_not_read()
    {
        var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(_root, "manifest.json")))!;
        m.Files.Add(new ManifestEntry(Path.Combine("..", "outside.txt"), 1, null, "00"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(m));
        var p = Assert.Single(Run().Problems);
        Assert.Equal("unreadable", p.Kind);
        Assert.Contains("outside", p.Detail);
    }

    [Fact]
    public void Verify_writes_nothing_into_the_backup()
    {
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
        Run();
        var after = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
        Assert.Equal(before, after);
    }
}
