using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SupernoteSharp.Business;
using SupernoteSharp.Common;
using SupernoteSharp.Entities;

namespace SupernoteBridge.Core;

public record PdfMeta(string Path, int Pages, int[] Annotated);

/// <summary>
/// RAM-only annotated-PDF rendering: the PDF and its sibling .mark are downloaded
/// into memory, PDF pages are rasterized with PDFium and the mark's handwriting
/// pages are multiply-blended on top. Only the most recently opened PDF is cached.
///
/// Mark geometry: the device fits a PDF page into its 1404x1872 screen preserving
/// aspect ratio (letterboxed, centered); the mark bitmap covers the full screen, so
/// the content box is cropped out and scaled onto the rasterized page.
/// </summary>
public sealed class PdfPreviewService
{
    private const int ScreenW = 1404; // A6X2 Nomad
    private const int ScreenH = 1872;
    private const int RenderDpi = 150;

    private readonly IDeviceTransport _transport;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed class CachedPdf
    {
        public required string Path;
        public required string CacheKey;
        public required byte[] PdfBytes;
        public required int PageCount;
        public Converter.ImageConverter? MarkConverter;
        public required Dictionary<int, int> MarkPageByPdfPage; // 0-based pdf page -> mark page index
        public readonly Dictionary<int, byte[]> Pages = new();
        public readonly Dictionary<int, byte[]> Thumbs = new();
    }

    private CachedPdf? _current;

    public PdfPreviewService(IDeviceTransport transport) => _transport = transport;

    /// <summary>Drops the cached PDF (source switched between device and a backup).</summary>
    public void Invalidate()
    {
        _lock.Wait();
        try { _current = null; }
        finally { _lock.Release(); }
    }

    public async Task<PdfMeta> GetMetaAsync(string devicePath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var pdf = await EnsureLoadedAsync(devicePath, ct);
            return new PdfMeta(devicePath, pdf.PageCount, pdf.MarkPageByPdfPage.Keys.Order().ToArray());
        }
        finally { _lock.Release(); }
    }

    public async Task<byte[]> GetPagePngAsync(string devicePath, int page, bool thumb = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var pdf = await EnsureLoadedAsync(devicePath, ct);
            if (page < 0 || page >= pdf.PageCount) throw new ArgumentOutOfRangeException(nameof(page));
            if (thumb && pdf.Thumbs.TryGetValue(page, out var cachedThumb)) return cachedThumb;
            if (!thumb && pdf.Pages.TryGetValue(page, out var cached)) return cached;

            // rasterize the PDF page (in memory)
            using var pageMs = new MemoryStream();
            PDFtoImage.Conversion.SavePng(pageMs, pdf.PdfBytes, page: page,
                options: new PDFtoImage.RenderOptions(Dpi: RenderDpi));
            byte[] png;

            if (pdf.MarkConverter is not null && pdf.MarkPageByPdfPage.TryGetValue(page, out int markIdx))
            {
                using var pageImg = Image.Load<Rgba32>(pageMs.ToArray());
                var overlay = Converter.ImageConverter.BuildVisibilityOverlay(
                    VisibilityOverlay.Default, VisibilityOverlay.Default, VisibilityOverlay.Default,
                    VisibilityOverlay.Default, VisibilityOverlay.Default);
                using var markImg = pdf.MarkConverter.Convert(markIdx, overlay);

                // content box of the PDF page inside the device screen
                double aspect = (double)pageImg.Width / pageImg.Height;
                int cw, chh, ox, oy;
                if (aspect < (double)ScreenW / ScreenH)
                {
                    chh = ScreenH; cw = (int)Math.Round(ScreenH * aspect);
                    ox = (ScreenW - cw) / 2; oy = 0;
                }
                else
                {
                    cw = ScreenW; chh = (int)Math.Round(ScreenW / aspect);
                    ox = 0; oy = (ScreenH - chh) / 2;
                }
                markImg.Mutate(x => x
                    .Crop(new Rectangle(ox, oy, cw, chh))
                    .Resize(pageImg.Width, pageImg.Height));
                pageImg.Mutate(x => x.DrawImage(markImg, PixelColorBlendingMode.Multiply, 1f));

                using var outMs = new MemoryStream();
                pageImg.SaveAsPng(outMs);
                png = outMs.ToArray();
            }
            else
            {
                png = pageMs.ToArray();
            }

            if (pdf.Pages.Count >= 40) pdf.Pages.Clear(); // bound full-page RAM cache; thumbs stay
            pdf.Pages[page] = png;

            if (thumb)
            {
                var t = ThumbUtil.MakeThumb(png);
                pdf.Thumbs[page] = t;
                return t;
            }
            return png;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Whole PDF with mark annotations burned in, as a new PDF (JPEG pages @150dpi). RAM-only.</summary>
    public async Task<byte[]> ExportAnnotatedAsync(string devicePath, CancellationToken ct = default)
    {
        // page count first (also loads/caches the pdf+mark pair)
        var meta = await GetMetaAsync(devicePath, ct);
        var pages = new List<(byte[] Jpeg, int W, int H)>(meta.Pages);
        for (int i = 0; i < meta.Pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            var png = await GetPagePngAsync(devicePath, i, thumb: false, ct);
            using var img = Image.Load<Rgba32>(png);
            using var jms = new MemoryStream();
            img.SaveAsJpeg(jms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
            pages.Add((jms.ToArray(), img.Width, img.Height));
        }
        return JpegPdfWriter.Write(pages, RenderDpi);
    }

    private async Task<CachedPdf> EnsureLoadedAsync(string devicePath, CancellationToken ct)
    {
        var pdfStat = await _transport.StatAsync(devicePath, ct)
            ?? throw new FileNotFoundException("Not found on device.", devicePath);
        var markStat = await _transport.StatAsync(devicePath + ".mark", ct);

        string key = $"{pdfStat.Size}|{pdfStat.Modified:o}|{markStat?.Size}|{markStat?.Modified:o}";
        if (_current is { } c && c.Path == devicePath && c.CacheKey == key)
            return c;

        using var pdfMs = new MemoryStream();
        await _transport.DownloadAsync(devicePath, pdfMs, ct);
        byte[] pdfBytes = pdfMs.ToArray();
        int pageCount = PDFtoImage.Conversion.GetPageCount(pdfBytes);

        Converter.ImageConverter? markConverter = null;
        var map = new Dictionary<int, int>();
        if (markStat is not null)
        {
            using var markMs = new MemoryStream();
            await _transport.DownloadAsync(devicePath + ".mark", markMs, ct);
            markMs.Position = 0;
            var parser = new Parser();
            Notebook markNotebook = parser.LoadNotebook(markMs, Policy.Loose);
            markConverter = new Converter.ImageConverter(markNotebook, DefaultColorPalette.Grayscale);

            // footer keys "PAGE<n>" in insertion order correspond to mark pages 0..k
            // (SupernoteXParser.GetPageAddresses uses the same enumeration order)
            int markIdx = 0;
            foreach (string k in markNotebook.Metadata.Footer.Keys)
            {
                var m = Regex.Match(k, @"^PAGE(\d+)$");
                if (!m.Success) continue;
                int pdfPage0 = int.Parse(m.Groups[1].Value) - 1;
                if (pdfPage0 >= 0 && pdfPage0 < pageCount) map[pdfPage0] = markIdx;
                markIdx++;
            }
        }

        _current = new CachedPdf
        {
            Path = devicePath,
            CacheKey = key,
            PdfBytes = pdfBytes,
            PageCount = pageCount,
            MarkConverter = markConverter,
            MarkPageByPdfPage = map,
        };
        return _current;
    }
}
