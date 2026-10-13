using System.Text.Json;
using MediaDevices;

// Spike S0.1/S0.2 driver:
//   spike dump                          -> enumerate WPD devices, dump full tree of the Supernote to spike-out\tree.json
//   spike fetch <devicePath> [local]    -> download one file from the device
//   spike render <localNote.note>       -> render page 0 to PNG + metadata JSON via SupernoteSharp

string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "spike-out");
outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";

switch (cmd)
{
    case "dump": Dump(); break;
    case "fetch": Fetch(args[1], args.Length > 2 ? args[2] : null); break;
    case "render": Render(args[1]); break;
    case "cachetest": CacheTest(); break;
    case "markmeta": MarkMeta(args[1]); break;
    case "markpage": MarkPageStats(args[1], int.Parse(args[2])); break;
    case "pdfcheck":
        var pdfBytes = File.ReadAllBytes(args[1]);
        int pc = PDFtoImage.Conversion.GetPageCount(pdfBytes);
        using (var outMs = new MemoryStream())
        {
            PDFtoImage.Conversion.SavePng(outMs, pdfBytes, page: 0, options: new PDFtoImage.RenderOptions(Dpi: 60));
            Console.WriteLine($"PDFium parsed OK: {pc} pages, page 1 rendered {outMs.Length} bytes PNG");
        }
        break;
    default: Console.Error.WriteLine($"unknown command: {cmd}"); Environment.Exit(2); break;
}

MediaDevice ConnectSupernote()
{
    var devices = MediaDevice.GetDevices().ToList();
    Console.WriteLine($"WPD devices found: {devices.Count}");
    foreach (var d in devices)
        Console.WriteLine($"  - '{d.FriendlyName}' desc='{d.Description}' mfr='{d.Manufacturer}'");

    var sn = devices.FirstOrDefault(d =>
        (d.FriendlyName ?? "").Contains("supernote", StringComparison.OrdinalIgnoreCase) ||
        (d.Description ?? "").Contains("supernote", StringComparison.OrdinalIgnoreCase) ||
        (d.FriendlyName ?? "").Contains("nomad", StringComparison.OrdinalIgnoreCase) ||
        (d.Manufacturer ?? "").Contains("ratta", StringComparison.OrdinalIgnoreCase));

    if (sn is null)
    {
        Console.Error.WriteLine("No Supernote found. Is it plugged in and unlocked?");
        Environment.Exit(1);
    }
    sn.Connect();
    Console.WriteLine($"Connected: {sn.FriendlyName} (model={sn.Model}, serial={sn.SerialNumber})");
    return sn;
}

void Dump()
{
    using var device = ConnectSupernote();
    var entries = new List<object>();
    var errors = new List<object>();
    int fileCount = 0, dirCount = 0;

    void Walk(MediaDirectoryInfo dir)
    {
        List<MediaFileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! enum failed at '{dir.FullName}': {ex.Message}");
            errors.Add(new { path = dir.FullName, error = ex.Message });
            return;
        }

        foreach (var info in children)
        {
            try
            {
                if (info is MediaDirectoryInfo sub)
                {
                    dirCount++;
                    entries.Add(new { path = info.FullName, dir = true });
                    Walk(sub);
                }
                else
                {
                    fileCount++;
                    entries.Add(new
                    {
                        path = info.FullName,
                        dir = false,
                        size = info.Length,
                        mtime = info.LastWriteTime?.ToString("o")
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! entry failed at '{info.FullName}': {ex.Message}");
                errors.Add(new { path = info.FullName, error = ex.Message });
            }
        }
    }

    var root = device.GetRootDirectory();
    Walk(root);

    var dump = new
    {
        friendlyName = device.FriendlyName,
        model = device.Model,
        serial = device.SerialNumber,
        firmware = device.FirmwareVersion,
        dumpedAt = DateTime.Now.ToString("o"),
        dirCount,
        fileCount,
        errorCount = errors.Count,
        errors,
        entries
    };

    string jsonPath = Path.Combine(outDir, "tree.json");
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"S0.1 OK: {dirCount} dirs, {fileCount} files -> {jsonPath}");
    device.Disconnect();
}

// RAM-only: prints .mark metadata to console, nothing written to disk
void MarkMeta(string devicePath)
{
    using var device = ConnectSupernote();
    using var ms = new MemoryStream();
    device.DownloadFile(devicePath, ms);
    device.Disconnect();
    ms.Position = 0;

    var parser = new SupernoteSharp.Business.Parser();
    var metadata = parser.ParseMetadata(ms, SupernoteSharp.Common.Policy.Loose);
    Console.WriteLine($"signature: {metadata.Signature}");
    Console.WriteLine($"total pages in mark: {metadata.TotalPages}");
    Console.WriteLine("--- header ---");
    foreach (var kv in metadata.Header) Console.WriteLine($"  {kv.Key} = {kv.Value}");
    Console.WriteLine("--- footer keys ---");
    foreach (var kv in metadata.Footer) Console.WriteLine($"  {kv.Key} = {kv.Value}");
    for (int i = 0; i < Math.Min(2, metadata.Pages.Count); i++)
    {
        Console.WriteLine($"--- page[{i}] ---");
        foreach (var kv in metadata.Pages[i])
        {
            if (kv.Value is List<Dictionary<string, object>> layers)
            {
                for (int L = 0; L < layers.Count; L++)
                {
                    Console.WriteLine($"  layer[{L}]:");
                    foreach (var lkv in layers[L])
                        Console.WriteLine($"    {lkv.Key} = {(lkv.Value?.ToString()?.Length > 100 ? lkv.Value.ToString()![..100] + "…" : lkv.Value)}");
                }
            }
            else
                Console.WriteLine($"  {kv.Key} = {(kv.Value?.ToString()?.Length > 100 ? kv.Value.ToString()![..100] + "…" : kv.Value)}");
        }
    }
}

// RAM-only: renders one mark page and prints pixel stats to console (no disk writes)
void MarkPageStats(string devicePath, int pageIdx)
{
    using var device = ConnectSupernote();
    using var ms = new MemoryStream();
    device.DownloadFile(devicePath, ms);
    device.Disconnect();
    ms.Position = 0;

    var parser = new SupernoteSharp.Business.Parser();
    var notebook = parser.LoadNotebook(ms, SupernoteSharp.Common.Policy.Loose);
    Console.WriteLine($"mark pages: {notebook.TotalPages}");
    for (int i = 0; i < notebook.TotalPages; i++)
    {
        var pg = notebook.Page(i);
        var l0 = pg.Layer(0);
        Console.WriteLine($"page[{i}] mainlayer content: {l0?.Content?.Length.ToString() ?? "null"} bytes, totalpath: {pg.TotalPath?.Length.ToString() ?? "null"}, style: {pg.Style}");
    }
    var converter = new SupernoteSharp.Business.Converter.ImageConverter(notebook, SupernoteSharp.Business.DefaultColorPalette.Grayscale);
    var overlay = SupernoteSharp.Business.Converter.ImageConverter.BuildVisibilityOverlay(
        SupernoteSharp.Common.VisibilityOverlay.Default, SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default, SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default);
    using var img = converter.Convert(pageIdx, overlay);
    Console.WriteLine($"rendered: {img.Width}x{img.Height}, format: {img.GetType().Name}, pixel: {img.PixelType.BitsPerPixel}bpp");

    using var rgba = img.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgba32>();
    long transparent = 0, white = 0, ink = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
    rgba.ProcessPixelRows(acc =>
    {
        for (int y = 0; y < acc.Height; y++)
        {
            var row = acc.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x];
                if (p.A == 0) transparent++;
                else if (p.R > 245 && p.G > 245 && p.B > 245) white++;
                else { ink++; if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
            }
        }
    });
    Console.WriteLine($"transparent: {transparent}, white: {white}, ink: {ink}");
    if (ink > 0) Console.WriteLine($"ink bbox: x[{minX}..{maxX}] y[{minY}..{maxY}]");

    var hist = new Dictionary<byte, long>();
    rgba.ProcessPixelRows(acc =>
    {
        for (int y = 0; y < acc.Height; y++)
        {
            var row = acc.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            { hist.TryGetValue(row[x].R, out long c); hist[row[x].R] = c + 1; }
        }
    });
    Console.WriteLine("gray histogram (value: count):");
    foreach (var kv in hist.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key}: {kv.Value}");
}

void CacheTest()
{
    var a = MediaDevice.GetDevices().ToList();
    var b = MediaDevice.GetDevices().ToList();
    bool sameRefs = a.Any(x => b.Any(y => ReferenceEquals(x, y)));
    Console.WriteLine($"count a={a.Count} b={b.Count}, shared instances across calls: {sameRefs}");

    var sn1 = a.First(d => (d.Description ?? "").Contains("Supernote", StringComparison.OrdinalIgnoreCase));
    sn1.Connect();
    Console.WriteLine($"sn1 connected: {sn1.IsConnected}");

    var sn2 = b.First(d => d.DeviceId == sn1.DeviceId);
    Console.WriteLine($"sn1/sn2 same object: {ReferenceEquals(sn1, sn2)}");
    sn2.Dispose();
    Console.WriteLine($"after sn2.Dispose(): sn1.IsConnected = {sn1.IsConnected}");
    try
    {
        var root = sn1.GetRootDirectory();
        Console.WriteLine($"sn1 still usable, root children: {root.EnumerateFileSystemInfos().Count()}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"sn1 BROKEN after other-instance dispose: {ex.GetType().Name}: {ex.Message}");
    }
}

void Fetch(string devicePath, string? localPath)
{
    using var device = ConnectSupernote();
    localPath ??= Path.Combine(outDir, Path.GetFileName(devicePath.Replace('\\', '/')));
    using (var fs = File.Create(localPath))
        device.DownloadFile(devicePath, fs);
    Console.WriteLine($"Fetched '{devicePath}' -> {localPath} ({new FileInfo(localPath).Length} bytes)");
    device.Disconnect();
}

void Render(string notePath)
{
    using var fs = new FileStream(notePath, FileMode.Open, FileAccess.Read);
    var parser = new SupernoteSharp.Business.Parser();
    var metadata = parser.ParseMetadata(fs, SupernoteSharp.Common.Policy.Loose);
    var notebook = parser.LoadNotebook(fs, SupernoteSharp.Common.Policy.Loose);

    string metaPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(notePath) + ".meta.json");
    File.WriteAllText(metaPath, metadata.ToJson());
    Console.WriteLine($"Pages: {notebook.TotalPages}, signature: {metadata.Signature} -> {metaPath}");

    var converter = new SupernoteSharp.Business.Converter.ImageConverter(notebook, SupernoteSharp.Business.DefaultColorPalette.Grayscale);
    var overlay = SupernoteSharp.Business.Converter.ImageConverter.BuildVisibilityOverlay(
        SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default,
        SupernoteSharp.Common.VisibilityOverlay.Default);
    using SixLabors.ImageSharp.Image image = converter.Convert(0, overlay);
    string pngPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(notePath) + ".page0.png");
    SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, pngPath);
    Console.WriteLine($"S0.2 OK: page 0 -> {pngPath}");
}
