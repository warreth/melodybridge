using MelodyBridge.Application;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Services;
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

// Register all MelodyBridge services
builder.Services.AddMelodyBridge();
builder.Services.AddJellyfinSync();

// Dev panel (singleton, off by default — enable via DevPanel__Enabled=true env var)
var devPanel = new DevPanelService();
devPanel.Enabled = builder.Configuration.GetValue<bool>("DevPanel:Enabled");
builder.Services.AddSingleton(devPanel);

var app = builder.Build();

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
app.MapFallbackToPage("/_Host");

app.Run();
