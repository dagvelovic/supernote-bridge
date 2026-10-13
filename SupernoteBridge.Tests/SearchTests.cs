using SupernoteBridge.Core;

namespace SupernoteBridge.Tests;

public class SearchTextTests
{
    [Theory]
    [InlineData("Čćšžđ ČĆŠŽĐ", "ccszd ccszd")]
    [InlineData("Kibernetičkoj Sigurnosti", "kibernetickoj sigurnosti")]
    [InlineData("Ärger Øre ñ é", "arger øre n e")] // ø has no decomposition: kept, lower-cased
    [InlineData("Šuma 🌲 i ✔ Đak", "suma 🌲 i ✔ dak")]   // emoji = surrogate pairs: kept as they are (real notes contain them)
    public void Fold_removes_case_and_diacritics_one_char_per_char(string input, string expected)
    {
        Assert.Equal(expected, SearchText.Fold(input));
        Assert.Equal(input.Length, SearchText.Fold(input).Length);
    }

    [Fact]
    public void All_words_must_match_in_any_order()
    {
        string t = SearchText.Fold("Sastanak s Ivanom o proračunu za 2026.");
        Assert.True(SearchText.MatchesAll(t, SearchText.Words("proracun SASTANAK")));
        Assert.False(SearchText.MatchesAll(t, SearchText.Words("proracun marko")));
        Assert.False(SearchText.MatchesAll(t, SearchText.Words("   ")));
    }

    [Fact]
    public void Snippet_marks_point_at_the_original_words()
    {
        string filler = string.Join(" ", Enumerable.Repeat("riječ", 40));
        string orig = filler + " Đurđevac je lijep grad, Đurđevac opet " + filler;
        string folded = SearchText.Fold(orig);
        var (text, marks) = SearchText.Snippet(orig, folded, SearchText.Words("durdevac"));
        Assert.StartsWith("…", text);
        Assert.EndsWith("…", text);
        Assert.Equal(2, marks.Count);
        Assert.All(marks, m => Assert.Equal("Đurđevac", text.Substring(m.Start, m.Length)));
    }
}

public class SearchIndexServiceTests
{
    private static SearchResult WaitReady(SearchIndexService s, string q, string? under = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (s.GetStatus().Phase is "scanning" or "indexing" && sw.Elapsed.TotalSeconds < 20) Thread.Sleep(10);
        return s.Search(q, under);
    }

    [Fact]
    public void Names_are_searched_across_the_tree_and_scoped_by_folder()
    {
        var src = new TreeSource();
        var s = new SearchIndexService(src);
        Assert.True(s.Refresh());
        var r = WaitReady(s, "proracun");
        Assert.Equal("ready", r.Index.Phase);
        Assert.Equal(new[] { "\\Internal shared storage\\Note\\Proračun", "\\Internal shared storage\\Note\\Proračun\\proračun_2026.note" },
            r.Names.Select(n => n.Path));
        Assert.Single(s.Search("proracun 2026").Names);
        Assert.Empty(s.Search("proracun", "\\SanDisk SD card").Names);
        Assert.Single(s.Search("note", "\\Internal shared storage\\Note\\Proračun").Names); // only what is inside the folder
    }

    [Fact]
    public void Unreadable_notes_are_counted_not_fatal_and_reset_clears_everything()
    {
        var src = new TreeSource();
        var s = new SearchIndexService(src);
        s.Refresh();
        var r = WaitReady(s, "x");
        Assert.Equal((2, 2, 2), (r.Index.NotesTotal, r.Index.NotesDone, r.Index.FailedNotes)); // fake bytes are not .note files
        Assert.Empty(r.Hits);
        s.Reset();
        Assert.Equal("idle", s.GetStatus().Phase);
        Assert.Empty(s.Search("proracun").Names);
    }

    /// <summary>A tiny device tree; .note content is not a real note (parsing fails by design).</summary>
    private sealed class TreeSource : IDeviceTransport
    {
        private static readonly string[] Paths =
        {
            "\\Internal shared storage\\",
            "\\Internal shared storage\\Note\\",
            "\\Internal shared storage\\Note\\Proračun\\",
            "\\Internal shared storage\\Note\\Proračun\\proračun_2026.note",
            "\\Internal shared storage\\Note\\ideje.note",
            "\\SanDisk SD card\\",
            "\\SanDisk SD card\\EBOOK.pdf",
        };
        public bool IsConnected => true;
        public DeviceInfo? Info => null;
        public event EventHandler? Attached { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }
        public Task<IReadOnlyList<DeviceEntry>> ListAsync(string path, CancellationToken ct = default)
        {
            string prefix = path == "\\" ? "\\" : path + "\\";
            IReadOnlyList<DeviceEntry> r = Paths
                .Where(p => p.StartsWith(prefix) && p.Length > prefix.Length && !p[prefix.Length..].TrimEnd('\\').Contains('\\'))
                .Select(p => { bool dir = p.EndsWith('\\'); string full = p.TrimEnd('\\');
                    return new DeviceEntry(full, full[(full.LastIndexOf('\\') + 1)..], dir, dir ? null : 4, null); })
                .ToList();
            return Task.FromResult(r);
        }
        public Task DownloadAsync(string p, Stream t, CancellationToken ct = default) { t.Write("FAKE"u8); return Task.CompletedTask; }
        public Task UploadAsync(Stream s, long l, string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string p, bool r = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string p, string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DeviceEntry?> StatAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
