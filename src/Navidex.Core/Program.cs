using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Navidex.Client;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core;
using Navidex.Core.Access;
using Navidex.Core.Api;
using Navidex.Core.Artwork;
using Navidex.Core.Catalogue;
using Navidex.Core.Data;
using Navidex.Core.Diagnostics;
using Navidex.Core.Events;
using Navidex.Core.Import;
using Navidex.Core.Library;
using Navidex.Core.Listening;
using Navidex.Core.Plugins;
using Navidex.Core.Requests;
using Navidex.Core.Tasks;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(NavidexOptions.Section).Get<NavidexOptions>() ?? new NavidexOptions();
builder.Services.Configure<NavidexOptions>(builder.Configuration.GetSection(NavidexOptions.Section));
builder.Services.AddOptions<PluginHttpOptions>();

Directory.CreateDirectory(options.DataPath);
builder.Services.AddSingleton<LibraryCache>();
builder.Services.AddSingleton<EventFeed>();
builder.Services.AddDbContext<NavidexDbContext>((services, db) => db
    .UseSqlite(builder.Configuration.GetConnectionString("Navidex") ?? $"Data Source={Path.Combine(options.DataPath, "navidex.db")}",
        NavidexDbContext.Sqlite)
    .AddInterceptors(services.GetRequiredService<LibraryCache>(), services.GetRequiredService<EventFeed>()));

// The activity log the web app shows. Registered as a provider so everything logged reaches it.
var activity = new ActivityLog();
builder.Services.AddSingleton(activity);
builder.Logging.AddProvider(new ActivityLogProvider(activity));
builder.Services.AddHostedService<LogArchive>();

// Who may call the API: the admin's session, apps' keys, and the local network if that's let in. The keys that sign
// sessions are kept with the data, so a restart doesn't sign everyone out.
builder.Services.AddNavidexAccess();
builder.Services.AddDataProtection()
    .SetApplicationName("Navidex")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")));

builder.Services.ConfigureHttpJsonOptions(json => NavidexJson.Configure(json.SerializerOptions));

// Plugins
builder.Services.AddSingleton<PluginCatalog>();
builder.Services.AddSingleton<PluginHost>();
builder.Services.AddScoped<IndexPlugins>();
builder.Services.AddScoped<ConnectorPlugins>();
builder.Services.AddScoped<ArtworkPlugins>();
builder.Services.AddSingleton<ArtworkCache>();
builder.Services.AddHostedService<ArtworkWarmer>();
builder.Services.AddScoped<RetrievalPlugins>();
builder.Services.AddHttpClient(RetrievalPlugins.HttpClientName, http => http.Timeout = TimeSpan.FromMinutes(2));

// Catalogue and library
builder.Services.AddScoped<CatalogueService>();
builder.Services.AddScoped<CatalogueRefresher>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<LibraryMatcher>();
builder.Services.AddScoped<LibraryView>();
builder.Services.AddScoped<LibraryRemover>();
builder.Services.AddScoped<CatalogueSearch>();

// Background work
builder.Services.AddSingleton<TaskSignal>();
builder.Services.AddScoped<TaskQueue>();
builder.Services.AddScoped<ITaskHandler, LibraryScanTask>();
builder.Services.AddScoped<ITaskHandler, ArtistRefreshTask>();
builder.Services.AddHostedService<TaskWorker>();

// Requests: fetch, verify, import with beets, tell the media server
builder.Services.Configure<ImportWorkerOptions>(builder.Configuration.GetSection("Navidex:ImportWorker"));
builder.Services.AddOptions<RequestPipelineOptions>();
builder.Services.AddHttpClient<IImportWorker, HttpImportWorker>(HttpImportWorker.Configure);
builder.Services.AddSingleton<RequestSignal>();
builder.Services.AddSingleton<LibraryReading>();
builder.Services.AddSingleton<ScanFeed>();
builder.Services.AddSingleton<RequestCancellations>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<RequestPlanner>();
builder.Services.AddScoped<RequestPipeline>();
builder.Services.AddSingleton<StreamSigner>();
builder.Services.AddScoped<ListenNowService>();
builder.Services.AddHostedService<ListenNowSweeper>();
builder.Services.AddHostedService<RequestWorker>();

// Events: announced on the event stream and sent to webhooks
builder.Services.AddHttpClient(WebhookSender.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddHostedService(services => services.GetRequiredService<WebhookSender>());
builder.Services.AddHostedService<EventAnnouncer>();

builder.Services.AddNavidexOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NavidexDbContext>();
    await db.Database.MigrateAsync();
    // Write-ahead logging, so pages keep reading while a scan writes. It's stored in the file, so this is once.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await IndexPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
    await ArtworkPlugins.EnsureDefaultAsync(db, scope.ServiceProvider.GetRequiredService<PluginCatalog>());
    await app.Services.GetRequiredService<StreamSigner>().InitialiseAsync(db);
}

// The way back in when the password is lost: start once with Navidex__ResetLogin=true, then set a new one.
if (options.ResetLogin)
{
    await app.Services.GetRequiredService<AccessService>().SaveLoginAsync(null, CancellationToken.None);
    app.Logger.LogWarning("The admin login was removed, as ResetLogin is set. Open the web app to set a new one, then unset ResetLogin");
}

// Work out the library's views in the background, so the first visit after starting is quick too.
_ = app.Services.GetRequiredService<LibraryCache>().WarmAsync(CancellationToken.None);

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

app.UseAuthentication();
app.UseApiAccess();

// The spec is part of the public contract, so it's served in every environment.
app.MapOpenApi();
app.MapNavidexApiReference();

var api = app.MapGroup("/api/v1");
api.MapSystemEndpoints();
api.MapLibraryEndpoints();
api.MapCatalogueEndpoints();
api.MapRequestEndpoints();
api.MapListenEndpoints();
api.MapPluginEndpoints();
api.MapAccessEndpoints();
api.MapWebhookEndpoints();

// The web app: Blazor WebAssembly, served from the same origin and talking only to the public API.
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
