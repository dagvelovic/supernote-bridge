using System.Text;

namespace SupernoteBridge.Core;

/// <summary>
/// Minimal multi-page PDF writer embedding one JPEG per page (DCTDecode).
/// Exists so annotated-PDF exports stay memory-bounded: VectSharp's RasterImage
/// holds raw RGB (≈6.5 MB/page), which explodes on 400-page documents; JPEG
/// streams stay ~200 KB/page.
/// </summary>
internal static class JpegPdfWriter
{
    internal static byte[] Write(IReadOnlyList<(byte[] Jpeg, int W, int H)> pages, double dpi)
    {
        var ms = new MemoryStream();
        var offsets = new List<long>(); // xref offsets, object 1..n
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        void BeginObj(int _) { offsets.Add(ms.Position); }

        W("%PDF-1.4\n%âãÏÓ\n");

        int n = pages.Count;
        // object numbering: 1 catalog, 2 pages; per page i (0-based): 3+3i page, 4+3i image, 5+3i content
        BeginObj(1);
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObj(2);
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"));
        W($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var (jpeg, w, h) = pages[i];
            double pw = w * 72.0 / dpi, ph = h * 72.0 / dpi;

            BeginObj(3 + 3 * i);
            W($"{3 + 3 * i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw:0.##} {ph:0.##}] " +
              $"/Resources << /XObject << /Im0 {4 + 3 * i} 0 R >> >> /Contents {5 + 3 * i} 0 R >>\nendobj\n");

            BeginObj(4 + 3 * i);
            W($"{4 + 3 * i} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
              $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            ms.Write(jpeg);
            W("\nendstream\nendobj\n");

            string content = $"q {pw:0.##} 0 0 {ph:0.##} 0 0 cm /Im0 Do Q";
            BeginObj(5 + 3 * i);
            W($"{5 + 3 * i} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        long xrefPos = ms.Position;
        int total = offsets.Count + 1;
        W($"xref\n0 {total}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            W($"{off:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return ms.ToArray();
    }
}
