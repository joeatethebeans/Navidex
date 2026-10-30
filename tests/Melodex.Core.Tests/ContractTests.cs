using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Retrieval;

namespace Melodex.Core.Tests;

public class ContractTests
{
    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.2", true)]
    [InlineData("1.3", false)]
    [InlineData("0.9", false)]
    [InlineData("2.0", false)]
    public void Core_accepts_plugins_for_its_major_version_up_to_its_minor(string plugin, bool supported)
    {
        var core = new ContractVersion(1, 2);

        Assert.Equal(supported, core.Supports(ContractVersion.Parse(plugin)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.x")]
    [InlineData("1.0.0")]
    public void Malformed_contract_versions_are_rejected(string value)
    {
        Assert.Throws<FormatException>(() => ContractVersion.Parse(value));
    }

    [Fact]
    public void Manifest_round_trips_with_its_settings_schema()
    {
        const string json = """
            {
              "id": "melodex.retrieval.ytdlp",
              "name": "yt-dlp",
              "version": "0.1.0",
              "type": "retrieval",
              "contractVersion": "1.0",
              "capabilities": ["fetch-track", "fetch-album"],
              "settingsSchema": { "type": "object", "properties": { "format": { "enum": ["opus", "m4a"] } } },
              "access": { "network": ["youtube.com"], "files": ["staging (write)"] },
              "retrieval": { "formats": ["opus", "m4a"], "maxBitrateKbps": 256 }
            }
            """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, RetrievalJson.Options)!;

        Assert.Equal(PluginType.Retrieval, manifest.Type);
        Assert.Equal(["opus", "m4a"], manifest.Retrieval!.Formats);
        Assert.Equal("object", manifest.SettingsSchema!.Value.GetProperty("type").GetString());
        Assert.Contains("\"type\":\"retrieval\"", JsonSerializer.Serialize(manifest, RetrievalJson.Options));
    }

    [Fact]
    public void Retrieval_wire_format_is_camel_case_and_omits_nulls()
    {
        var target = new RetrievalTarget(RetrievalKind.Track, "Song", "Artist", DurationSeconds: 201.5,
            Ids: [new ProviderId(Providers.Isrc, "GBAYE0601498")]);

        var json = JsonSerializer.Serialize(target, RetrievalJson.Options);

        Assert.Equal("""{"kind":"track","title":"Song","artist":"Artist","durationSeconds":201.5,"ids":[{"provider":"isrc","value":"GBAYE0601498"}]}""", json);
    }
}
