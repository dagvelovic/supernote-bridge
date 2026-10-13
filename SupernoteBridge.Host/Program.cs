using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.FileProviders;
using SupernoteBridge.Core;

// last-resort crash visibility: a silent exit leaves no trace otherwise
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

// ---- settings.json beside the exe (portable: no %APPDATA%, no registry) ----
string exeDir = AppContext.BaseDirectory;
string settingsPath = Path.Combine(exeDir, "settings.json");
var settings = new AppSettings();
if (File.Exists(settingsPath))
{
    try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath), AppSettings.JsonOpts) ?? settings; }
    catch (Exception ex) { Console.Error.WriteLine($"[WARN] settings.json unreadable, using defaults: {ex.Message}"); }
}
else
{
    try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, AppSettings.JsonOpts)); }
    catch { /* read-only medium (e.g. CD) — run with defaults */ }
}

// ---- CLI flags override settings.json ----
// --port <n>  --open / --no-open  --backup <dir>  --backup-on-attach  --quit  --open-backup <dir>  --verify <dir>
string? backupDirArg = null, openBackupArg = null, verifyArg = null;
bool backupOnAttach = false, quitAfterBackup = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" when i + 1 < args.Length: settings.Port = int.Parse(args[++i]); break;
        case "--open": settings.Open = true; break;
        case "--no-open": settings.Open = false; break;
        case "--backup" when i + 1 < args.Length: backupDirArg = args[++i]; break;
        case "--backup-on-attach": backupOnAttach = true; break;
        case "--quit": quitAfterBackup = true; break;
        case "--open-backup" when i + 1 < args.Length: openBackupArg = args[++i]; break;
        case "--verify" when i + 1 < args.Length: verifyArg = args[++i]; break;
        case "--help" or "-h" or "/?":
            Console.WriteLine("SupernoteBridge [--port n] [--open|--no-open] [--backup <dir>] [--backup-on-attach] [--quit] [--open-backup <backup folder>] [--verify <backup folder>]");
            return 0;
    }
}
// headless backup check: no web server; exit 0 = OK, 1 = problems, 2 = not a backup
if (verifyArg is not null)
{
    string root = Path.GetFileName(verifyArg).Equals(BackupFolderTransport.ManifestName, StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(Path.GetFullPath(verifyArg))! : Path.GetFullPath(verifyArg);
    if (!File.Exists(Path.Combine(root, BackupFolderTransport.ManifestName)))
    {
        Console.Error.WriteLine($"[verify] not a backup (no manifest.json): {root}");
        return 2;
    }
    var verifier = new VerifyService();
    verifier.Start(root);
    VerifyStatus vs;
    do { Thread.Sleep(1000); vs = verifier.GetStatus(); Console.WriteLine($"[verify] {vs.DoneFiles}/{vs.TotalFiles} {vs.BytesHashed / 1048576} MB"); }
    while (vs.Phase == "verifying");
    foreach (var pr in vs.Problems) Console.WriteLine($"[verify] {pr.Kind.ToUpperInvariant(),-10} {pr.Path}{(pr.Detail is null ? "" : " — " + pr.Detail)}");
    Console.WriteLine(vs.Phase != "done" ? $"[verify] {vs.Phase}: {vs.Error}"
        : $"[verify] {vs.OkFiles}/{vs.TotalFiles} files OK, {vs.Problems.Count} problem(s) — {root}");
    return vs.Phase == "done" && vs.Problems.Count == 0 ? 0 : 1;
}

string backupRoot = backupDirArg ?? settings.BackupRoot ?? Path.Combine(exeDir, "BACKUP");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{settings.Port}");
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = null;  // device files can be huge
    o.AllowSynchronousIO = true;         // MediaDevices copies streams synchronously
});
builder.Services.AddSingleton<WpdTransport>();
// viewer/export services see the active source (device or an opened backup folder) ...
builder.Services.AddSingleton(sp => new SourceRouter(sp.GetRequiredService<WpdTransport>()));
builder.Services.AddSingleton<IDeviceTransport>(sp => sp.GetRequiredService<SourceRouter>());
builder.Services.AddSingleton<NoteRenderService>();
builder.Services.AddSingleton<PdfPreviewService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<VerifyService>();
builder.Services.AddSingleton<SearchIndexService>();
// ... but backups always come from the device itself
builder.Services.AddSingleton(sp => new BackupService(sp.GetRequiredService<WpdTransport>(), backupRoot));
// ... and restore always writes to the device itself
builder.Services.AddSingleton(sp => new RestoreService(sp.GetRequiredService<WpdTransport>()));

var app = builder.Build();

// switching source drops the RAM caches of the previous one
var sourceRouter = app.Services.GetRequiredService<SourceRouter>();
sourceRouter.SourceChanged += (_, _) =>
{
    app.Services.GetRequiredService<NoteRenderService>().Invalidate();
    app.Services.GetRequiredService<PdfPreviewService>().Invalidate();
    app.Services.GetRequiredService<SearchIndexService>().Reset();
};
// the search index belongs to one device session too (forwarded only while the device is the source)
sourceRouter.Attached += (_, _) => app.Services.GetRequiredService<SearchIndexService>().Reset();
sourceRouter.Detached += (_, _) => app.Services.GetRequiredService<SearchIndexService>().Reset();

if (openBackupArg is not null) sourceRouter.UseBackup(openBackupArg); // start in offline mode

// backup (offline) mode is read-only; paths may not escape the backup folder
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ReadOnlySourceException ex) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync(ex.Message); }
    catch (UnauthorizedAccessException ex) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync(ex.Message); }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && !ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 404; }
});

// static UI: physical wwwroot in dev, embedded resources in the published single exe
IFileProvider staticFiles = Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot"))
    ? new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "wwwroot"))
    : new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFiles });

// --- device status ---
app.MapGet("/api/device", (WpdTransport t, SourceRouter src) =>
    Results.Json(new { connected = t.IsConnected, info = t.Info, backup = src.Backup }));

// --- source: live device or a backup folder opened from disk (offline mode) ---
// Native open dialog on the backup's manifest.json; STA thread because the host has no UI loop.
app.MapPost("/api/source/open", (SourceRouter src, BackupService backup) =>
{
    string? picked = null;
    var th = new Thread(() =>
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open a Supernote backup — pick manifest.json inside the backup folder",
            Filter = "Supernote backup (manifest.json)|manifest.json",
            InitialDirectory = src.Backup is { } b ? Path.GetDirectoryName(b.Root)!
                : Directory.Exists(backup.DefaultRoot) ? backup.DefaultRoot : "",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK) picked = dlg.FileName;
    });
    th.SetApartmentState(ApartmentState.STA);
    th.Start();
    th.Join();
    if (picked is null) return Results.NoContent();
    src.UseBackup(picked);
    return Results.Json(src.Backup);
});

// --- verify the open backup against its manifest (reads only) ---
app.MapPost("/api/verify/start", (SourceRouter src, VerifyService verify) =>
    src.Backup is not { } b ? Results.BadRequest("no backup is open")
    : verify.Start(b.Root) ? Results.Ok()
    : Results.Conflict("verify already running"));

app.MapPost("/api/verify/cancel", (VerifyService verify) => { verify.Cancel(); return Results.Ok(); });

app.MapGet("/api/verify/status", (VerifyService verify) => Results.Json(verify.GetStatus()));

// --- search: names + recognized handwriting of the active source (index in RAM only) ---
app.MapGet("/api/search", (string q, string? under, SearchIndexService search, SourceRouter src, HttpResponse response) =>
{
    if (!src.IsConnected) return Results.StatusCode(503);
    if (search.GetStatus().Phase is "idle" or "error") search.Refresh(); // first search builds the index
    response.Headers.CacheControl = "no-store"; // snippets are device content
    return Results.Json(search.Search(q, under));
});

app.MapPost("/api/search/refresh", (SearchIndexService search, SourceRouter src) =>
    !src.IsConnected ? Results.StatusCode(503) : search.Refresh() ? Results.Ok() : Results.Conflict("indexing already running"));

app.MapGet("/api/search/status", (SearchIndexService search) => Results.Json(search.GetStatus()));

// --- restore from the open backup to the device: plan (compare, writes nothing) → confirm → restore ---
app.MapPost("/api/restore/plan", (string path, SourceRouter src, WpdTransport device, RestoreService restore) =>
    src.BackupSource is not { } backup ? Results.BadRequest("no backup is open")
    : !device.IsConnected ? Results.StatusCode(503)
    : restore.Plan(backup, path) ? Results.Ok()
    : Results.Conflict("a restore is already running"));

app.MapPost("/api/restore/start", (bool overwrite, WpdTransport device, RestoreService restore) =>
    !device.IsConnected ? Results.StatusCode(503)
    : restore.Restore(overwrite) ? Results.Ok()
    : Results.Conflict("nothing planned, or a restore is already running"));

app.MapPost("/api/restore/cancel", (RestoreService restore) => { restore.Cancel(); return Results.Ok(); });

app.MapGet("/api/restore/status", (RestoreService restore) => Results.Json(restore.GetStatus()));

app.MapPost("/api/source/device", (SourceRouter src) => { src.UseDevice(); return Results.Ok(); });

// --- list directory ---
app.MapGet("/api/fs", async (string? path, IDeviceTransport t) =>
{
    try { return Results.Json(await t.ListAsync(path ?? "\\")); }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
});

// --- download file (explicit save by the user) ---
app.MapGet("/api/file", async (string path, IDeviceTransport t, HttpResponse response) =>
{
    var stat = await t.StatAsync(path);
    if (stat is null || stat.IsDirectory) return Results.NotFound();

    response.Headers.CacheControl = "no-store";
    response.ContentType = "application/octet-stream";
    response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(stat.Name)}\"";
    if (stat.Size is long size) response.ContentLength = size;
    await t.DownloadAsync(path, response.Body);
    return Results.Empty;
});

// --- inline preview stream (PDF etc.); no-store so the browser leaves no disk trace ---
app.MapGet("/api/preview", async (string path, IDeviceTransport t, HttpResponse response) =>
{
    var stat = await t.StatAsync(path);
    if (stat is null || stat.IsDirectory) return Results.NotFound();

    response.Headers.CacheControl = "no-store";
    response.ContentType = Path.GetExtension(stat.Name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
    if (stat.Size is long size) response.ContentLength = size;
    await t.DownloadAsync(path, response.Body);
    return Results.Empty;
});

// --- upload file (raw body PUT; path = full device path incl. filename) ---
// Buffered in RAM by design: no temp files on disk, ever (security requirement).
app.MapPut("/api/file", async (string path, HttpRequest request, IDeviceTransport t) =>
{
    try
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        ms.Position = 0;
        await t.UploadAsync(ms, ms.Length, path);
        return Results.Ok(new { path, size = ms.Length });
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
});

// --- rename file/dir in place ---
app.MapPost("/api/rename", async (string path, string newName, IDeviceTransport t) =>
{
    try { await t.RenameAsync(path, newName); return Results.Ok(); }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

// --- .note preview (RAM-only rendering) ---
app.MapGet("/api/note/meta", async (string path, NoteRenderService notes) =>
{
    try { return Results.Json(await notes.GetMetaAsync(path)); }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

app.MapGet("/api/note/page", async (string path, int n, bool? thumb, NoteRenderService notes, HttpResponse response) =>
{
    try
    {
        var png = await notes.GetPagePngAsync(path, n, thumb ?? false);
        response.Headers.CacheControl = "no-store";
        return Results.Bytes(png, "image/png");
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
    catch (ArgumentOutOfRangeException) { return Results.BadRequest("page out of range"); }
});

// --- delete file/dir ---
app.MapDelete("/api/file", async (string path, bool? recursive, IDeviceTransport t) =>
{
    try { await t.DeleteAsync(path, recursive ?? false); return Results.Ok(); }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
});

// --- annotated PDF preview (PDF pages rasterized in RAM, .mark strokes blended on top) ---
app.MapGet("/api/pdf/meta", async (string path, PdfPreviewService pdfs) =>
{
    try { return Results.Json(await pdfs.GetMetaAsync(path)); }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

app.MapGet("/api/pdf/page", async (string path, int n, bool? thumb, PdfPreviewService pdfs, HttpResponse response) =>
{
    try
    {
        var png = await pdfs.GetPagePngAsync(path, n, thumb ?? false);
        response.Headers.CacheControl = "no-store";
        return Results.Bytes(png, "image/png");
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
    catch (ArgumentOutOfRangeException) { return Results.BadRequest("page out of range"); }
});

// --- backup ---
// Native folder picker; runs on an STA thread because the host has no UI message loop.
app.MapPost("/api/backup/pickfolder", (BackupService backup) =>
{
    string? picked = null;
    var t = new Thread(() =>
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to create the timestamped Supernote backup folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(backup.DefaultRoot) ? backup.DefaultRoot : "",
        };
        if (dlg.ShowDialog() == DialogResult.OK) picked = dlg.SelectedPath;
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    t.Join();
    return picked is null ? Results.NoContent() : Results.Json(new { path = picked });
});

app.MapPost("/api/backup/start", (string? root, BackupService backup, WpdTransport t) =>
    !t.IsConnected ? Results.StatusCode(503)
    : backup.Start(root) ? Results.Ok()
    : Results.Conflict("backup already running"));

app.MapPost("/api/backup/cancel", (BackupService backup) => { backup.Cancel(); return Results.Ok(); });

app.MapGet("/api/backup/status", (BackupService backup) => Results.Json(backup.GetStatus()));

// --- note export (single, RAM-only) ---
app.MapGet("/api/note/export.pdf", async (string path, bool? vector, NoteRenderService notes, HttpResponse response) =>
{
    try
    {
        var pdf = await notes.GetPdfAsync(path, vector ?? true); // vector is the default (decision 2026-08-20)
        response.Headers.CacheControl = "no-store";
        string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/')) + ".pdf";
        response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(name)}\"";
        return Results.Bytes(pdf, "application/pdf");
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

app.MapGet("/api/note/export.txt", async (string path, NoteRenderService notes, HttpResponse response) =>
{
    try
    {
        var text = await notes.GetTextAsync(path);
        response.Headers.CacheControl = "no-store";
        string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/')) + ".txt";
        response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(name)}\"";
        return Results.Text(text.Length > 0 ? text : "(no recognized text in this note)", "text/plain");
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

// --- annotated PDF export (mark strokes burned in) ---
app.MapGet("/api/pdf/export.pdf", async (string path, PdfPreviewService pdfs, HttpResponse response) =>
{
    try
    {
        var pdf = await pdfs.ExportAnnotatedAsync(path);
        response.Headers.CacheControl = "no-store";
        string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/')) + "_annotated.pdf";
        response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(name)}\"";
        return Results.Bytes(pdf, "application/pdf");
    }
    catch (DeviceNotConnectedException) { return Results.StatusCode(503); }
    catch (FileNotFoundException) { return Results.NotFound(); }
});

// --- bulk export (device folder -> PDFs on disk) ---
app.MapPost("/api/export/start", (string src, string target, bool? vector, ExportService export, SourceRouter t) =>
    !t.IsConnected ? Results.StatusCode(503)
    : IsInside(target, t.Backup?.Root) ? Results.BadRequest("target is inside the open backup — backups are read-only")
    : export.Start(src, target, vector ?? true) ? Results.Ok()
    : Results.Conflict("export already running"));

static bool IsInside(string path, string? root)
{
    if (root is null) return false;
    string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    return full.Equals(root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

app.MapPost("/api/export/cancel", (ExportService export) => { export.Cancel(); return Results.Ok(); });

app.MapGet("/api/export/status", (ExportService export) => Results.Json(export.GetStatus()));

// ---- headless / automation modes ----
var transport = app.Services.GetRequiredService<WpdTransport>();
var backupSvc = app.Services.GetRequiredService<BackupService>();

if (backupOnAttach)
{
    transport.Attached += (_, _) =>
    {
        Console.WriteLine("[backup-on-attach] device attached, starting backup…");
        backupSvc.Start(backupRoot);
    };
}

int exitCode = 0;
if (backupDirArg is not null)
{
    // scripted backup: wait for the device, run one backup, optionally quit
    _ = Task.Run(async () =>
    {
        var t0 = DateTime.Now;
        while (!transport.IsConnected)
        {
            if ((DateTime.Now - t0).TotalSeconds > 120)
            {
                Console.Error.WriteLine("[backup] no device within 120 s, giving up");
                exitCode = 2;
                if (quitAfterBackup) { await app.StopAsync(); }
                return;
            }
            await Task.Delay(2000);
        }
        Console.WriteLine("[backup] device connected, starting backup…");
        backupSvc.Start(backupRoot);
        BackupStatus s;
        do { await Task.Delay(2000); s = backupSvc.GetStatus(); Console.WriteLine($"[backup] {s.Phase} {s.DoneFiles}/{s.TotalFiles} {s.BytesCopied / 1048576} MB"); }
        while (s.Phase is "scanning" or "copying");
        Console.WriteLine($"[backup] {s.Phase}: {s.CopiedFiles} files, {s.BytesCopied / 1048576} MB -> {s.TargetDir}" +
                          (s.Warnings.Count > 0 ? $" ({s.Warnings.Count} warnings)" : ""));
        exitCode = s.Phase == "done" ? 0 : 1;
        if (quitAfterBackup) await app.StopAsync();
    });
}

if (settings.Open && backupDirArg is null)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{settings.Port}") { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] could not open browser: {ex.Message}"); }
    });
}

app.Run();
return exitCode;

internal sealed class AppSettings
{
    public int Port { get; set; } = 8099;
    public bool Open { get; set; } = true;
    public string? BackupRoot { get; set; }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
