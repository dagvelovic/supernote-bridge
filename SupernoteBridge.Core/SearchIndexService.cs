using SupernoteSharp.Business;
using SupernoteSharp.Common;

namespace SupernoteBridge.Core;

public record SearchIndexStatus(
    string Phase,           // idle | scanning | indexing | ready | error
    int Files, int Folders,
    int NotesTotal, int NotesDone,
    int PagesWithText, int PagesWithoutText,
    int FailedNotes,
    IReadOnlyList<string> Failed,   // "path: reason" of notes that could not be read
    string? Error);

public record NameHit(string Path, string Name, bool IsDirectory, long? Size);

public record TextHit(string Path, string Name, int Page, string Snippet, IReadOnlyList<int[]> Marks);

public record SearchResult(IReadOnlyList<NameHit> Names, IReadOnlyList<TextHit> Hits, bool Truncated, SearchIndexStatus Index);

/// <summary>
/// Search over the active source (device or open backup): file/folder names, and the device's
/// handwriting-recognition text of every .note page. The index lives in RAM only (security policy:
/// recognized text is device content) and is rebuilt per session; a refresh re-walks the tree and
/// re-reads only notes whose size or date changed. A source switch drops it.
/// </summary>
public sealed class SearchIndexService
{
    private const int MaxNames = 200, MaxHits = 200;

    private sealed record Entry(string Path, string Name, string FoldedName, bool IsDirectory, long? Size, DateTime? Modified);
    private sealed record NoteText(long? Size, DateTime? Modified, string[] Pages, string[] Folded, int WithoutText);

    private readonly IDeviceTransport _source;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _running;
    private int _generation;

    private string _phase = "idle";
    private string? _error;
    private List<Entry> _entries = new();
    private Dictionary<string, NoteText> _notes = new(StringComparer.OrdinalIgnoreCase);
    private int _notesTotal, _notesDone, _failed;
    private List<string> _failedPaths = new();

    public SearchIndexService(IDeviceTransport source) => _source = source;

    public SearchIndexStatus GetStatus()
    {
        lock (_sync)
            return new SearchIndexStatus(_phase, _entries.Count(e => !e.IsDirectory), _entries.Count(e => e.IsDirectory),
                _notesTotal, _notesDone,
                _notes.Values.Sum(n => n.Pages.Length - n.WithoutText), _notes.Values.Sum(n => n.WithoutText),
                _failed, _failedPaths.ToList(), _error);
    }

    /// <summary>Drops the whole index (source switched, device re-attached).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _generation++;
            _phase = "idle"; _error = null;
            _entries = new(); _notes = new(StringComparer.OrdinalIgnoreCase);
            _notesTotal = _notesDone = _failed = 0;
            _failedPaths = new();
        }
    }

    /// <summary>Builds or refreshes the index in the background. False if already running.</summary>
    public bool Refresh()
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return false;
            _cts = new CancellationTokenSource();
            _phase = "scanning"; _error = null;
            int gen = _generation;
            var token = _cts.Token;
            _running = Task.Run(() => BuildAsync(gen, token));
            return true;
        }
    }

    private async Task BuildAsync(int gen, CancellationToken ct)
    {
        try
        {
            var entries = new List<Entry>();
            await WalkAsync("\\", entries, ct);
            var notes = entries.Where(e => !e.IsDirectory && e.Name.EndsWith(".note", StringComparison.OrdinalIgnoreCase)).ToList();
            lock (_sync)
            {
                if (gen != _generation) return;
                _entries = entries;
                _notesTotal = notes.Count; _notesDone = 0; _failed = 0; _failedPaths = new();
                // forget notes that are gone; keep unchanged ones
                var keep = notes.Where(n => _notes.TryGetValue(n.Path, out var t) && t.Size == n.Size && t.Modified == n.Modified)
                    .ToDictionary(n => n.Path, n => _notes[n.Path], StringComparer.OrdinalIgnoreCase);
                _notes = keep;
                _notesDone = keep.Count;
                _phase = "indexing";
            }

            foreach (var n in notes)
            {
                ct.ThrowIfCancellationRequested();
                lock (_sync) if (_notes.ContainsKey(n.Path)) continue;
                NoteText? text = null;
                try { text = await ReadNoteAsync(n, ct); }
                catch (OperationCanceledException) { throw; }
                catch (DeviceNotConnectedException) { throw; }
                catch (Exception ex) { lock (_sync) { _failed++; _failedPaths.Add($"{n.Path}: {ex.GetType().Name}: {ex.Message}"); } }
                lock (_sync)
                {
                    if (gen != _generation) return;
                    if (text is not null) _notes[n.Path] = text;
                    _notesDone++;
                }
            }
            lock (_sync) if (gen == _generation) _phase = "ready";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { lock (_sync) if (gen == _generation) { _phase = "error"; _error = ex.Message; } }
    }

    private async Task<NoteText> ReadNoteAsync(Entry n, CancellationToken ct)
    {
        using var ms = new MemoryStream(n.Size is > 0 and <= int.MaxValue ? (int)n.Size : 0);
        await _source.DownloadAsync(n.Path, ms, ct);
        ms.Position = 0;
        var notebook = new Parser().LoadNotebook(ms, Policy.Loose);
        var converter = new Converter.TextConverter(notebook);
        var pages = new string[notebook.TotalPages];
        int without = 0;
        for (int i = 0; i < pages.Length; i++)
        {
            try { pages[i] = converter.Convert(i) ?? ""; }
            catch { pages[i] = ""; } // recognition not available for this page
            if (string.IsNullOrWhiteSpace(pages[i])) without++;
        }
        return new NoteText(n.Size, n.Modified, pages, pages.Select(SearchText.Fold).ToArray(), without);
    }

    private async Task WalkAsync(string path, List<Entry> into, CancellationToken ct)
    {
        IReadOnlyList<DeviceEntry> list;
        try { list = await _source.ListAsync(path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not DeviceNotConnectedException) { return; } // e.g. Android\data
        foreach (var e in list)
        {
            ct.ThrowIfCancellationRequested();
            into.Add(new Entry(e.Path, e.Name, SearchText.Fold(e.Name), e.IsDirectory, e.Size, e.Modified));
            if (e.IsDirectory) await WalkAsync(e.Path, into, ct);
        }
    }

    /// <param name="under">Optional folder (device path) to limit the search to.</param>
    public SearchResult Search(string query, string? under = null)
    {
        var words = SearchText.Words(query);
        string prefix = string.IsNullOrEmpty(under) || under == "\\" ? "" : under.TrimEnd('\\') + "\\";
        bool In(string p) => prefix.Length == 0 || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        List<Entry> entries; List<KeyValuePair<string, NoteText>> notes;
        lock (_sync) { entries = _entries; notes = _notes.ToList(); }

        var names = entries.Where(e => In(e.Path) && SearchText.MatchesAll(e.FoldedName, words))
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxNames + 1).Select(e => new NameHit(e.Path, e.Name, e.IsDirectory, e.Size)).ToList();

        var hits = new List<TextHit>();
        foreach (var (path, note) in notes.Where(n => In(n.Key)).OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
        {
            for (int p = 0; p < note.Pages.Length && hits.Count <= MaxHits; p++)
            {
                if (!SearchText.MatchesAll(note.Folded[p], words)) continue;
                var (snip, marks) = SearchText.Snippet(note.Pages[p], note.Folded[p], words);
                hits.Add(new TextHit(path, path[(path.LastIndexOf('\\') + 1)..], p, snip,
                    marks.Select(m => new[] { m.Start, m.Length }).ToList()));
            }
        }
        bool truncated = names.Count > MaxNames || hits.Count > MaxHits;
        return new SearchResult(names.Take(MaxNames).ToList(), hits.Take(MaxHits).ToList(), truncated, GetStatus());
    }
}
