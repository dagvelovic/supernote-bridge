using SixLabors.ImageSharp;
using SupernoteSharp.Business;
using SupernoteSharp.Common;
using SupernoteSharp.Entities;

namespace SupernoteBridge.Core;

public record NoteMeta(string Path, int Pages, string Signature);

/// <summary>
/// RAM-only .note rendering. Device content never touches disk:
/// the .note is downloaded into a MemoryStream, parsed and rendered in memory,
/// and PNG bytes are served straight from memory. Only the most recently opened
/// note is cached (bytes + rendered pages); it is replaced when another note is
/// opened and disappears with the process.
/// </summary>
public sealed class NoteRenderService
{
    private readonly IDeviceTransport _transport;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed class CachedNote
    {
        public required string Path;
        public required long? Size;
        public required DateTime? Modified;
        public required Notebook Notebook;
        public required Converter.ImageConverter Converter;
        public readonly Dictionary<int, byte[]> Pages = new();
        public readonly Dictionary<int, byte[]> Thumbs = new();
    }

    private CachedNote? _current;

    public NoteRenderService(IDeviceTransport transport)
    {
        _transport = transport;
        _transport.Detached += (_, _) => { }; // cache stays valid: it is a snapshot in RAM
    }

    /// <summary>Drops the cached note (source switched between device and a backup).</summary>
    public void Invalidate()
    {
        _lock.Wait();
        try { _current = null; }
        finally { _lock.Release(); }
    }

    public async Task<NoteMeta> GetMetaAsync(string devicePath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var note = await EnsureLoadedAsync(devicePath, ct);
            return new NoteMeta(devicePath, note.Notebook.TotalPages, note.Notebook.Signature);
        }
        finally { _lock.Release(); }
    }

    public async Task<byte[]> GetPagePngAsync(string devicePath, int page, bool thumb = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var note = await EnsureLoadedAsync(devicePath, ct);
            if (page < 0 || page >= note.Notebook.TotalPages)
                throw new ArgumentOutOfRangeException(nameof(page));

            if (thumb && note.Thumbs.TryGetValue(page, out var cachedThumb))
                return cachedThumb;
            if (!thumb && note.Pages.TryGetValue(page, out var cached))
                return cached;

            var overlay = Converter.ImageConverter.BuildVisibilityOverlay(
                VisibilityOverlay.Default, VisibilityOverlay.Default, VisibilityOverlay.Default,
                VisibilityOverlay.Default, VisibilityOverlay.Default);
            using var image = note.Converter.Convert(page, overlay);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var png = ms.ToArray();
            if (note.Pages.Count >= 40) note.Pages.Clear(); // bound full-page RAM cache; thumbs stay
            note.Pages[page] = png;

            if (thumb)
            {
                var t = ThumbUtil.MakeThumb(png);
                note.Thumbs[page] = t;
                return t;
            }
            return png;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Realtime-recognition text of all pages (empty string when the note has none).</summary>
    public async Task<string> GetTextAsync(string devicePath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var note = await EnsureLoadedAsync(devicePath, ct);
            var converter = new Converter.TextConverter(note.Notebook);
            var pages = new List<string>();
            for (int i = 0; i < note.Notebook.TotalPages; i++)
            {
                try { pages.Add(converter.Convert(i)); }
                catch { } // page recognition not finished (internal ConverterException) — skip page
            }
            return string.Join(Environment.NewLine + Environment.NewLine, pages.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
        finally { _lock.Release(); }
    }

    /// <summary>Whole note as PDF (vector = searchable strokes, default; raster otherwise). RAM-only.</summary>
    public async Task<byte[]> GetPdfAsync(string devicePath, bool vector, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var note = await EnsureLoadedAsync(devicePath, ct);
            var converter = new Converter.PdfConverter(note.Notebook, DefaultColorPalette.Grayscale);
            return converter.ConvertAll(vectorize: vector, enableLinks: true);
        }
        finally { _lock.Release(); }
    }

    private async Task<CachedNote> EnsureLoadedAsync(string devicePath, CancellationToken ct)
    {
        var stat = await _transport.StatAsync(devicePath, ct)
            ?? throw new FileNotFoundException("Not found on device.", devicePath);

        if (_current is { } c && c.Path == devicePath && c.Size == stat.Size && c.Modified == stat.Modified)
            return c;

        using var ms = new MemoryStream(stat.Size is long s and > 0 && s <= int.MaxValue ? (int)s : 0);
        await _transport.DownloadAsync(devicePath, ms, ct);
        ms.Position = 0;

        var parser = new Parser();
        var notebook = parser.LoadNotebook(ms, Policy.Loose);
        _current = new CachedNote
        {
            Path = devicePath,
            Size = stat.Size,
            Modified = stat.Modified,
            Notebook = notebook,
            Converter = new Converter.ImageConverter(notebook, DefaultColorPalette.Grayscale),
        };
        return _current;
    }
}
