using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace SupernoteBridge.Core;

internal static class ThumbUtil
{
    internal const int ThumbWidth = 160;

    /// <summary>Downscales a PNG (in memory) to minimap thumbnail width.</summary>
    internal static byte[] MakeThumb(byte[] fullPng)
    {
        using var img = Image.Load(fullPng);
        int h = Math.Max(1, (int)Math.Round((double)img.Height * ThumbWidth / img.Width));
        img.Mutate(x => x.Resize(ThumbWidth, h));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
}
