using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Melodex.Core.Data;

namespace Melodex.Core;

/// <summary>
/// Melodex was called Navidex. An install from then upgrades in place: its settings, database, plugins and API keys
/// carry on under the new name, with nothing to do by hand.
/// </summary>
public static class FromNavidex
{
    public const string OldName = "Navidex";
    private const string OldDatabase = "navidex.db";
    private const string OldPluginPrefix = "navidex.";

    /// <summary>
    /// Reads <c>Navidex__</c> environment variables as their <c>Melodex__</c> twins, so an old compose file keeps
    /// working. A <c>Melodex__</c> variable wins where both are set.
    /// </summary>
    public static void AddOldSettings(ConfigurationManager config)
    {
        var prefix = OldName + "__";
        var old = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(k => MelodexOptions.Section + ":" + k[prefix.Length..].Replace("__", ":"), k => Environment.GetEnvironmentVariable(k));
        if (old.Count == 0)
            return;

        IConfigurationBuilder builder = config;
        var source = new MemoryConfigurationSource { InitialData = old };
        var environment = builder.Sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        if (environment >= 0)
            builder.Sources.Insert(environment, source);
        else
            builder.Sources.Add(source);
    }

    /// <summary>Renames the database file from before, with its write-ahead log, unless there's a Melodex one already.</summary>
    public static bool MoveDatabase(string dataPath, string database)
    {
        var old = Path.Combine(dataPath, OldDatabase);
        if (File.Exists(database) || !File.Exists(old))
            return false;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(old + suffix))
                File.Move(old + suffix, database + suffix);
        return true;
    }

    /// <summary>
    /// Plugins Melodex ships were <c>navidex.*</c>, and so are the IDs an older plugin container still reports. Run on
    /// every start, as such a container may be added again.
    /// </summary>
    public static Task<int> RenamePluginsAsync(MelodexDbContext db) =>
        db.Database.ExecuteSqlRawAsync("UPDATE PluginInstances SET PluginId = 'melodex.' || substr(PluginId, 9) WHERE PluginId LIKE 'navidex.%'");

    /// <summary>The ID a plugin goes by now, for one that still reports its old one.</summary>
    public static string PluginId(string id) =>
        id.StartsWith(OldPluginPrefix, StringComparison.OrdinalIgnoreCase) ? "melodex." + id[OldPluginPrefix.Length..] : id;
}
