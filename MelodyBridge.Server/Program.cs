using MelodyBridge.Application;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.Configuration.GetValue<string>("AppBaseUrl") ?? "http://localhost:3333/")
});

// Register DB context (SQLite file in approot)
builder.Services.AddDbContext<MelodyBridgeDbContext>(options =>
    options.UseSqlite("Data Source=melodybridge.db"));
builder.Services.AddDbContextFactory<MelodyBridgeDbContext>(options =>
    options.UseSqlite("Data Source=melodybridge.db"));

// Register all MelodyBridge services
builder.Services.AddMelodyBridge();

// Register Jellyfin media server sync
builder.Services.AddJellyfinSync();

var app = builder.Build();

// Configure the HTTP request pipeline.
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
