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

// Register DB context (SQLite)
builder.Services.AddDbContextFactory<MelodyBridgeDbContext>(options =>
    options.UseSqlite("Data Source=melodybridge.db"));
builder.Services.AddScoped<MelodyBridgeDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>().CreateDbContext());

// ── Unified logging ──────────────────────────────────────
// Singleton log collector shared by all components
var logCollector = new LogCollector(maxEntries: 1000);
builder.Services.AddSingleton<ILogCollector>(logCollector);
builder.Services.AddSingleton<LogExporter>();

// Bridge the standard ILogger<T> pipeline into the log collector
// so providers, services, and controllers appear in the DevPanel
builder.Logging.AddProvider(new DevPanelLoggerProvider(logCollector));

// Register all MelodyBridge services
builder.Services.AddMelodyBridge();
builder.Services.AddJellyfinSync();

// Dev panel (singleton, off by default — enable via DevPanel__Enabled=true env var)
var devPanel = new DevPanelService(logCollector);
devPanel.Enabled = builder.Configuration.GetValue<bool>("DevPanel:Enabled");
builder.Services.AddSingleton(devPanel);

// Data Protection — keys live in the default container location, no volume needed.
// Existing Blazor circuits invalidate on restart, which is fine for a personal app.
builder.Services.AddDataProtection()
    .SetApplicationName("MelodyBridge");

var app = builder.Build();

// App settings the stores read at runtime. The database overrides
// appsettings so the Settings page wins.
using (var db = app.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>()
           .CreateDbContext())
{
    var savedSpectrum = db.DownloaderSettings.FirstOrDefault(s => s.Key == "spectrum_mode")?.Value
                        ?? app.Configuration["SpectrumAnalysis:Mode"] ?? "Fast";
    PlaylistStore.SpectrumVerification = () => savedSpectrum.ToLowerInvariant() switch
    {
        "off" => MelodyBridge.Infrastructure.Audio.SpectrumMode.Off,
        "thorough" => MelodyBridge.Infrastructure.Audio.SpectrumMode.Thorough,
        _ => MelodyBridge.Infrastructure.Audio.SpectrumMode.Fast,
    };

    FlareSolverrSolver.Url = db.DownloaderSettings.FirstOrDefault(s => s.Key == "flaresolverr_url")?.Value
                             ?? app.Configuration["FlareSolverr:Url"] ?? "off";
}

// Ensure database is created with all entities
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
}

// Configure pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
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
