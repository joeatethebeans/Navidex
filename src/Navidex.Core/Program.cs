using Microsoft.EntityFrameworkCore;
using Navidex.Core;
using Navidex.Core.Api;
using Navidex.Core.Data;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(NavidexOptions.Section).Get<NavidexOptions>() ?? new NavidexOptions();
builder.Services.Configure<NavidexOptions>(builder.Configuration.GetSection(NavidexOptions.Section));

Directory.CreateDirectory(options.DataPath);
builder.Services.AddDbContext<NavidexDbContext>(db =>
    db.UseSqlite(builder.Configuration.GetConnectionString("Navidex")
                 ?? $"Data Source={Path.Combine(options.DataPath, "navidex.db")}"));

builder.Services.AddOpenApi("v1", openApi => openApi.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Navidex API";
    document.Info.Description = "Find what's missing from a music library, fetch it through plugins, and listen now.";
    return Task.CompletedTask;
}));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<NavidexDbContext>().Database.MigrateAsync();

// The spec is part of the public contract, so it's served in every environment.
app.MapOpenApi();

app.MapGroup("/api/v1").MapSystemEndpoints();

app.Run();

public partial class Program;
