using System.Text.Json;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Connectors;

namespace Melodex.Plugins.Navidrome;

/// <summary>Entry point for the Navidrome connector.</summary>
public sealed class NavidromeConnectorFactory : IPluginFactory
{
    public const string PluginId = "melodex.connector.navidrome";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Navidrome",
        Version = "0.1.0",
        Type = PluginType.Connector,
        ContractVersion = ContractVersions.Connector.ToString(),
        Description = "Tells Navidrome to scan when new music is filed, and finds it there.",
        Homepage = "https://www.navidrome.org",
        Capabilities = [ConnectorCapabilities.FullScan, ConnectorCapabilities.Playlists, ConnectorCapabilities.Favourites, ConnectorCapabilities.LibraryRead],
        Access = new PluginAccess { Network = ["Your Navidrome server"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url", "username", "password"],
              "properties": {
                "url": {
                  "type": "string",
                  "format": "uri",
                  "title": "Server URL",
                  "description": "Where Melodex reaches Navidrome, for example http://navidrome:4533.",
                  "default": "http://navidrome:4533"
                },
                "username": {
                  "type": "string",
                  "title": "Username",
                  "description": "An admin user: only admins can start scans."
                },
                "password": {
                  "type": "string",
                  "title": "Password",
                  "writeOnly": true
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new NavidromeConnector(context);
}
