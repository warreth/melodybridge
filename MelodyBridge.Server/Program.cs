using MelodyBridge.Application;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Cloudflare;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Logging;
using MelodyBridge.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.Configuration.GetValue<string>("AppBaseUrl") ?? "http://localhost:3333/")
});

// Register DB context (SQLite). Default Timeout is the busy_timeout:
// boot runs migrations while background services open their own
// connections, and WAL plus a wait window absorbs the overlap instead
// of logging "error using the connection" and dropping the query.
// MELODYBRIDGE_DB moves the file for Docker, where /app holds the
// application and writable state lives under a mounted /app/state.
var dbFile = Environment.GetEnvironmentVariable("MELODYBRIDGE_DB") ?? "melodybridge.db";
builder.Services.AddDbContextFactory<MelodyBridgeDbContext>(options =>
    options.UseSqlite($"Data Source={dbFile};Default Timeout=30"));
builder.Services.AddScoped<MelodyBridgeDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>().CreateDbContext());

// ── Unified logging ──────────────────────────────────────
// Singleton log collector shared by all components
var logCollector = new LogCollector(maxEntries: 1000);
builder.Services.AddSingleton<ILogCollector>(logCollector);
builder.Services.AddSingleton<LogExporter>();

// Bridge the standard ILogger<T> pipeline into the log collector
// so providers, services, and controllers appear in the DevPanel
builder.Logging.AddDevPanel(logCollector);

// Register all MelodyBridge services
builder.Services.AddMelodyBridge();
builder.Services.AddMediaServerSyncs();

// Update check: dedicated client because GitHub requires a User-Agent
// header and we never want to share the app's own BaseAddress client.
builder.Services.AddHttpClient("github", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped(sp =>
    new UpdateCheckService(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("github")));

// Toasts: singleton so events raised anywhere reach the one layout.
builder.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
builder.Services.AddHostedService<MelodyBridge.Server.Services.ToastObserverService>();
// Watch the subscription plugins' logins even when nobody has the
// Plugins page open, so an expired token reaches the user as a toast and
// a persistent mark instead of a silent "no source" miss.
builder.Services.AddHostedService<MelodyBridge.Server.Services.PluginAuthWatchService>();

// ntfy push: best effort, own client with a short timeout so a dead
// topic can never stall a notification sweep.
builder.Services.AddHttpClient("ntfy", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<MelodyBridge.Server.Services.NtfyNotifier>();

// Serious background failures (auto-sync, scans, crashed runs) reach the
// toasts and ntfy through this sink.
builder.Services.AddSingleton<MelodyBridge.Core.IBackgroundFailureSink,
    MelodyBridge.Server.Services.BackgroundFailureSink>();

// Dev panel (singleton, off by default: enable via DevPanel__Enabled=true env var)
var devPanel = new DevPanelService(logCollector);
devPanel.Enabled = builder.Configuration.GetValue<bool>("DevPanel:Enabled");
builder.Services.AddSingleton(devPanel);

// Data Protection: keys live in the default container location, no volume needed.
// Existing Blazor circuits invalidate on restart, which is fine for a personal app.
builder.Services.AddDataProtection()
    .SetApplicationName("MelodyBridge");

var app = builder.Build();

// Ensure database is created with all entities before anything reads it,
// then upgrade pre-existing databases in place (added columns, ...).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
    await SchemaPatcher.PatchAsync(db);

    // Stale-claim recovery: a track claimed "in_progress" by a run that
    // died (crash, hard shutdown) can never be claimed again. Nothing can
    // be in flight at startup, so every stale claim goes back to pending.
    var stale = db.Tracks
        .Where(t => t.DownloadStatus == "in_progress")
        .ExecuteUpdate(s => s.SetProperty(t => t.DownloadStatus, "pending"));
    if (stale > 0)
        app.Logger.LogInformation("Reset {Count} stale in-progress download claim(s) to pending", stale);
}

// Reconcile playlist tracks with files on disk: relink what still
// exists, queue re-downloads for what vanished while the app was down.
{
    var reconciler = app.Services.GetRequiredService<MelodyBridge.Infrastructure.Scanning.LibraryReconciler>();
    await reconciler.ReconcileAllAsync();
}

// App settings the stores read at runtime. The database overrides
// appsettings so the Settings page wins.
using (var db = app.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>()
           .CreateDbContext())
{
    var savedSpectrum = db.DownloaderSettings.FirstOrDefault(s => s.Key == "spectrum_mode")?.Value
                        ?? app.Configuration["SpectrumAnalysis:Mode"];
    var savedGap = db.DownloaderSettings.FirstOrDefault(s => s.Key == "spectrum_class_gap")?.Value;
    var savedToleranceSeconds = db.DownloaderSettings.FirstOrDefault(s => s.Key == "quality_length_tolerance_seconds")?.Value;
    var savedTolerancePercent = db.DownloaderSettings.FirstOrDefault(s => s.Key == "quality_length_tolerance_percent")?.Value;
    var verification = MelodyBridge.Infrastructure.Audio.VerificationOptions.FromSettings(
        savedSpectrum, savedGap, savedToleranceSeconds, savedTolerancePercent);
    PlaylistStore.Verification = () => verification;

    FlareSolverrSolver.Url = db.DownloaderSettings.FirstOrDefault(s => s.Key == "flaresolverr_url")?.Value
                             ?? app.Configuration["FlareSolverr:Url"] ?? "auto";

    // Optional Spotify web cookie for fallback scraping: a database
    // setting on the Network tab, no environment variable needed.
    MelodyBridge.Infrastructure.Services.SpotifySourceProvider.WebCookie =
        db.DownloaderSettings.FirstOrDefault(s => s.Key == "spotify_cookie")?.Value;

    // EF command logs are off unless the user asked for them (Advanced page).
    MelodyBridge.Server.Services.DatabaseLogSwitch.Set(
        db.DownloaderSettings.FirstOrDefault(s => s.Key == MelodyBridge.Server.Services.DatabaseLogSwitch.SettingKey)?.Value is "true");
}

// Configure pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
// HTTPS redirection only makes sense when the app itself has an HTTPS
// listener (a direct certificate setup). Behind a reverse proxy the
// container speaks HTTP only: the middleware would log "Failed to
// determine the https port" on every start and can never redirect, so
// the proxy owns that redirect there.
if (HasHttpsEndpoint(app.Configuration))
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();

app.MapBlazorHub();
app.MapControllers();

// ── Log export endpoint ──────────────────────────────────
app.MapGet("/api/logs/export", (LogExporter exporter) =>
{
    var bytes = exporter.ExportToBytes();
    return Results.File(
        bytes,
        "text/plain; charset=utf-8",
        $"melodybridge-logs-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.txt");
});

app.MapFallbackToPage("/_Host");

app.Run();

/// <summary>
/// True when configuration adds an HTTPS listener: the URLS setting
/// (ASPNETCORE_URLS / URLS / appsettings) or a Kestrel endpoint.
/// </summary>
static bool HasHttpsEndpoint(IConfiguration configuration)
{
    if ((configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? string.Empty)
        .Contains("https://", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return configuration.GetSection("Kestrel:Endpoints").GetChildren()
        .Any(endpoint => endpoint["Url"] is { } url
            && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
