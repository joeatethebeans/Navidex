using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Navidex.Contracts.Plugins;
using Navidex.Contracts.Plugins.Artwork;
using Navidex.Contracts.Plugins.Connectors;
using Navidex.Contracts.Plugins.Index;
using Navidex.Core.Data;
using Navidex.Plugins.MusicBrainz;

namespace Navidex.Core.Plugins;

/// <summary>
/// Creates and caches live instances of configured in-process plugins. An instance is rebuilt when its settings
/// change; its HTTP client (and so its rate limit) lives as long as the instance.
/// </summary>
public sealed class PluginHost(
    PluginCatalog catalog,
    IOptions<NavidexOptions> options,
    IOptions<PluginHttpOptions> httpOptions,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<Guid, (string Settings, IPlugin Plugin)> _instances = new();

    public T Get<T>(PluginInstance instance) where T : class, IPlugin
    {
        var cached = _instances.AddOrUpdate(instance.Id,
            _ => (instance.SettingsJson, Create(instance)),
            (_, existing) => existing.Settings == instance.SettingsJson ? existing : (instance.SettingsJson, Create(instance)));
        return cached.Plugin as T
               ?? throw new InvalidOperationException($"Plugin {instance.PluginId} doesn't implement {typeof(T).Name}.");
    }

    /// <summary>
    /// The instance as it runs: with the settings of the plugin it borrows them from, if it does. Null when that
    /// plugin isn't set up, so this one can't run.
    /// </summary>
    public async Task<PluginInstance?> ResolveAsync(NavidexDbContext db, PluginInstance instance, CancellationToken ct)
    {
        if (catalog.Find(instance.PluginId)?.Manifest.UsesSettingsOf is not { } lender)
            return instance;
        var settings = await db.PluginInstances.Where(p => p.PluginId == lender).OrderBy(p => p.Priority)
            .Select(p => p.SettingsJson).FirstOrDefaultAsync(ct);
        return settings is null
            ? null
            : new PluginInstance
            {
                Id = instance.Id, PluginId = instance.PluginId, Type = instance.Type, Name = instance.Name,
                ContractVersion = instance.ContractVersion, SettingsJson = settings, Priority = instance.Priority, Enabled = instance.Enabled,
            };
    }

    private IPlugin Create(PluginInstance instance)
    {
        var plugin = catalog.Find(instance.PluginId)
                     ?? throw new InvalidOperationException($"Plugin {instance.PluginId} isn't installed.");
        if (plugin.Factory is null)
            throw new InvalidOperationException($"Plugin {instance.PluginId} can't be loaded: {plugin.Rejection}");

        var dataDirectory = Path.Combine(options.Value.DataPath, "plugins", instance.Id.ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        return plugin.Factory.Create(new PluginContext
        {
            Settings = JsonDocument.Parse(instance.SettingsJson).RootElement.Clone(),
            HttpClient = PluginHttp.CreateClient(httpOptions.Value, plugin.Manifest.MaxRequestsPerSecond),
            LoggerFactory = loggerFactory,
            DataDirectory = dataDirectory,
        });
    }
}

/// <summary>The active index plugin. v1 runs one index plugin at a time: the enabled instance with the lowest priority.</summary>
public sealed class IndexPlugins(NavidexDbContext db, PluginHost host)
{
    public async Task<(PluginInstance Instance, IIndexPlugin Plugin)> GetPrimaryAsync(CancellationToken cancellationToken)
    {
        var instance = await db.PluginInstances
                           .Where(p => p.Type == PluginType.Index && p.Enabled)
                           .OrderBy(p => p.Priority)
                           .FirstOrDefaultAsync(cancellationToken)
                       ?? throw new InvalidOperationException("No index plugin is set up.");
        return (instance, host.Get<IIndexPlugin>(instance));
    }

    /// <summary>On first run, sets up MusicBrainz as the index plugin, so the catalogue works out of the box.</summary>
    public static async Task EnsureDefaultAsync(NavidexDbContext db, PluginCatalog catalog)
    {
        if (await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Index))
            return;
        var manifest = catalog.Find(MusicBrainzIndexFactory.PluginId)!.Manifest;
        db.PluginInstances.Add(new PluginInstance
        {
            PluginId = manifest.Id,
            Type = PluginType.Index,
            Name = manifest.Name,
            ContractVersion = manifest.ContractVersion,
            SettingsJson = """{"server":"https://musicbrainz.org"}""",
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>The active media-server connector, if one is set up: the enabled instance with the lowest priority.</summary>
public sealed class ConnectorPlugins(NavidexDbContext db, PluginHost host)
{
    public async Task<(PluginInstance Instance, IMediaServerConnector Connector)?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var instance = await db.PluginInstances
            .Where(p => p.Type == PluginType.Connector && p.Enabled)
            .OrderBy(p => p.Priority)
            .FirstOrDefaultAsync(cancellationToken);
        return instance is null ? null : (instance, host.Get<IMediaServerConnector>(instance));
    }
}

/// <summary>The enabled artwork plugins that can picture a subject, in the user's order.</summary>
public sealed class ArtworkPlugins(NavidexDbContext db, PluginHost host, PluginCatalog catalog)
{
    public const string DefaultsSetting = "artwork.defaults";

    public async Task<IReadOnlyList<(PluginInstance Instance, IArtworkPlugin Plugin)>> ForAsync(ArtworkSubject subject, CancellationToken ct)
    {
        var capability = ArtworkCapabilities.For(subject);
        var instances = await db.PluginInstances.Where(p => p.Type == PluginType.Artwork && p.Enabled).OrderBy(p => p.Priority).ToListAsync(ct);
        var plugins = new List<(PluginInstance, IArtworkPlugin)>();
        foreach (var instance in instances)
        {
            if (catalog.Find(instance.PluginId) is not { Factory: not null } found || !found.Manifest.Capabilities.Contains(capability))
                continue;
            if (await host.ResolveAsync(db, instance, ct) is { } running)
                plugins.Add((instance, host.Get<IArtworkPlugin>(running)));
        }
        return plugins;
    }

    /// <summary>On first run, sets up the Cover Art Archive, so albums have covers out of the box. Once only, so removing it sticks.</summary>
    public static async Task EnsureDefaultAsync(NavidexDbContext db, PluginCatalog catalog)
    {
        if (await db.Settings.AnyAsync(s => s.Key == DefaultsSetting))
            return;
        if (!await db.PluginInstances.AnyAsync(p => p.Type == PluginType.Artwork) && catalog.Find(CoverArtArchiveFactory.PluginId) is { } found)
            db.PluginInstances.Add(new PluginInstance
            {
                PluginId = found.Manifest.Id,
                Type = PluginType.Artwork,
                Name = found.Manifest.Name,
                ContractVersion = found.Manifest.ContractVersion,
                SettingsJson = SettingsSchema.Defaults(found.Manifest.SettingsSchema).ToJsonString(),
            });
        db.Settings.Add(new Setting { Key = DefaultsSetting, ValueJson = "true" });
        await db.SaveChangesAsync();
    }
}
