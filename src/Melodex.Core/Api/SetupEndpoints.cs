using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Api;

/// <summary>The steps after making the admin account: the music folder, the media server, fetching and the first scan.</summary>
public static class SetupEndpoints
{
    private const string DoneKey = "setup.done";

    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder api)
    {
        var setup = api.MapGroup("/setup").WithTags("System").RequireScope(Scopes.Settings);

        setup.MapGet("", async (MelodexDbContext db, IOptions<MelodexOptions> options, CancellationToken ct) =>
            {
                var path = options.Value.LibraryPath;
                var (readable, files) = Count(path);
                var scanned = await db.BackgroundTasks.AnyAsync(t => t.Kind == LibraryScanTask.TaskKind && t.Status == BackgroundTaskStatus.Succeeded, ct);
                var done = await db.Settings.AnyAsync(s => s.Key == DoneKey, ct);
                // An install that had music before there were setup steps doesn't need them.
                if (!done && await db.LibraryFiles.AnyAsync(ct))
                {
                    await MarkDoneAsync(db, ct);
                    done = true;
                }
                return new SetupStatus(done, Path.GetFullPath(path), readable, files,
                    await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Connector, ct),
                    await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Retrieval, ct),
                    scanned);
            })
            .WithName("GetSetup")
            .WithSummary("How far setup has got: whether the music folder can be read and has music in it, and what's been added.");

        setup.MapPost("/done", async (MelodexDbContext db, CancellationToken ct) =>
            {
                await MarkDoneAsync(db, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Settings)
            .WithName("FinishSetup")
            .WithSummary("Setup is finished, or skipped: the web app stops showing its steps.");

        return api;
    }

    private static async Task MarkDoneAsync(MelodexDbContext db, CancellationToken ct)
    {
        if (await db.Settings.FindAsync([DoneKey], ct) is null)
        {
            db.Settings.Add(new Setting { Key = DoneKey, ValueJson = "true" });
            await db.SaveChangesAsync(ct);
        }
    }

    // A quick look, not a scan: enough to say the folder is right, without walking a whole big library.
    private static (bool Readable, int Files) Count(string path)
    {
        try
        {
            var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => LibraryScanner.AudioExtensions.Contains(Path.GetExtension(f)))
                .Take(SetupStatus.CountLimit)
                .Count();
            return (true, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, 0);
        }
    }
}
