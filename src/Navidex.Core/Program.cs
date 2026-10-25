using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core;
using Navidex.Core.Api;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;
using Navidex.Core.Diagnostics;
using Navidex.Core.Library;
using Navidex.Core.Plugins;
using Navidex.Core.Tasks;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(NavidexOptions.Section).Get<NavidexOptions>() ?? new NavidexOptions();
builder.Services.Configure<NavidexOptions>(builder.Configuration.GetSection(NavidexOptions.Section));
builder.Services.AddOptions<PluginHttpOptions>();

Directory.CreateDirectory(options.DataPath);
builder.Services.AddDbContext<NavidexDbContext>(db =>
    db.UseSqlite(builder.Configuration.GetConnectionString("Navidex")
                 ?? $"Data Source={Path.Combine(options.DataPath, "navidex.db")}"));

// The activity log the web app shows. Registered as a provider so everything logged reaches it.
var activity = new ActivityLog();
builder.Services.AddSingleton(activity);
builder.Logging.AddProvider(new ActivityLogProvider(activity));

builder.Services.ConfigureHttpJsonOptions(json => NavidexJson.Configure(json.SerializerOptions));

// Plugins
builder.Services.AddSingleton<PluginCatalog>();
builder.Services.AddSingleton<PluginHost>();
builder.Services.AddScoped<IndexPlugins>();

// Catalogue and library
builder.Services.AddScoped<CatalogueService>();
builder.Services.AddScoped<CatalogueRefresher>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<LibraryMatcher>();
builder.Services.AddScoped<LibraryView>();

// Background work
builder.Services.AddSingleton<TaskSignal>();
builder.Services.AddScoped<TaskQueue>();
builder.Services.AddScoped<ITaskHandler, LibraryScanTask>();
builder.Services.AddScoped<ITaskHandler, ArtistRefreshTask>();
builder.Services.AddHostedService<TaskWorker>();

builder.Services.AddOpenApi("v1", openApi => openApi.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Navidex API";
    document.Info.Description = "Find what's missing from a music library, fetch it through plugins, and listen now.";
    return Task.CompletedTask;
}));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
    await db.Database.MigrateAsync();
    await IndexPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
}

// A catalogue source that's busy or down is the client's cue to try again later, not a server error.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (IndexUnavailableException e) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new { title = "The catalogue source is busy", detail = e.Message, status = 503 });
    }
});

// The spec is part of the public contract, so it's served in every environment.
app.MapOpenApi();

var api = app.MapGroup("/api/v1");
api.MapSystemEndpoints();
api.MapLibraryEndpoints();
api.MapCatalogueEndpoints();

// The web app: Blazor WebAssembly, served from the same origin and talking only to the public API.
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
