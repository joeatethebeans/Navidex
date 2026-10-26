using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Navidex.Contracts.Plugins;
using Navidex.Core.Catalogue;
using Navidex.Core.Library;
using Navidex.Core.Plugins;
using Navidex.Plugins.MusicBrainz;

namespace Navidex.Core.Tests;

public class TextMatchTests
{
    [Theory]
    [InlineData("Doin’ It Right", "doin it right")]
    [InlineData("Björk", "bjork")]
    [InlineData("Simon & Garfunkel", "simon and garfunkel")]
    [InlineData("  Harder,  Better, Faster!  ", "harder better faster")]
    public void Normalise_folds_case_accents_and_punctuation(string text, string expected) =>
        Assert.Equal(expected, TextMatch.Normalise(text));

    [Theory]
    [InlineData("Glory Box (Album Version)", "glory box")]
    [InlineData("Roads [2014 Remaster]", "roads")]
    [InlineData("One More Time (Live)", "one more time live")]
    [InlineData("Around the World (Radio Edit)", "around the world radio edit")]
    public void Titles_lose_qualifiers_that_keep_the_recording_but_keep_ones_that_change_it(string title, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseTitle(title));

    [Theory]
    [InlineData("Random Access Memories (10th Anniversary Edition)", "random access memories")]
    [InlineData("Dummy [FLAC]", "dummy")]
    [InlineData("Homework (1997)", "homework")]
    public void Album_titles_lose_edition_and_format_tags(string album, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseAlbum(album));

    [Theory]
    [InlineData("Daft Punk feat. Pharrell Williams", "daft punk")]
    [InlineData("Daft Punk ft. Julian Casablancas", "daft punk")]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Bob Marley & The Wailers", "bob marley and the wailers")]
    public void Artists_lose_featured_artists_and_a_leading_the(string artist, string expected) =>
        Assert.Equal(expected, TextMatch.NormaliseArtist(artist));

    [Fact]
    public void Similarity_ignores_word_order()
    {
        Assert.Equal(1, TextMatch.Similarity("box glory", "glory box"));
        Assert.Equal(0, TextMatch.Similarity("", "glory box"));
        Assert.InRange(TextMatch.Similarity("glory box", "glory bok"), 0.85, 0.95);
    }
}

public class FuzzyScoreTests
{
    private static readonly FileTags Tags = new() { Title = "Glory Box", Artist = "Portishead" };

    private static double Score(double fileSeconds, double? listingSeconds, string title = "Glory Box", string artist = "Portishead") =>
        LibraryMatcher.Fuzzy(Tags, TimeSpan.FromSeconds(fileSeconds), title, title, artist,
            listingSeconds is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void Same_title_artist_and_length_is_a_sure_match() => Assert.Equal(1, Score(306, 305), 3);

    [Fact]
    public void Lengths_a_few_seconds_apart_still_match() => Assert.True(Score(306, 309) >= 0.99);

    [Fact]
    public void A_length_over_20_seconds_out_never_matches() => Assert.True(Score(381, 306) <= 0.6);

    [Fact]
    public void An_unknown_length_stays_under_the_default_threshold() => Assert.True(Score(306, null) < 0.85);

    [Fact]
    public void A_different_title_never_matches_even_with_the_right_length() =>
        Assert.True(Score(306, 306, title: "Knockin’ On Glory Box (Portishead vs. Guns N’ Roses)") <= 0.5);

    [Fact]
    public void A_featured_artist_credit_doesnt_count_against_a_match() =>
        Assert.Equal(1, Score(306, 306, artist: "Portishead feat. Someone"), 3);
}

public class PluginCatalogTests
{
    private static PluginCatalog Catalog(string pluginsPath) =>
        new(Options.Create(new NavidexOptions { PluginsPath = pluginsPath }), NullLogger<PluginCatalog>.Instance);

    [Fact]
    public void The_built_in_musicbrainz_index_is_found_and_usable()
    {
        var plugin = Catalog(Path.Combine(Path.GetTempPath(), "navidex-no-plugins")).Find(MusicBrainzIndexFactory.PluginId);

        Assert.NotNull(plugin);
        Assert.Equal("built-in", plugin.Source);
        Assert.Equal(PluginType.Index, plugin.Manifest.Type);
        Assert.Null(plugin.Rejection);
        Assert.NotNull(plugin.Factory);
    }

    [Fact]
    public void Files_in_the_plugins_folder_that_arent_plugins_are_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "navidex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "junk"));
        File.WriteAllText(Path.Combine(root, "junk", "notes.dll"), "not an assembly");
        try
        {
            Assert.Equal(
            [
                Navidex.Plugins.MusicBrainz.CoverArtArchiveFactory.PluginId, MusicBrainzIndexFactory.PluginId,
                Navidex.Plugins.Navidrome.NavidromeArtworkFactory.PluginId, Navidex.Plugins.Navidrome.NavidromeConnectorFactory.PluginId,
                Navidex.Plugins.FanartTv.FanartTvFactory.PluginId, Navidex.Plugins.Wikimedia.WikimediaCommonsFactory.PluginId,
                Navidex.Plugins.Deezer.DeezerFactory.PluginId,
            ], Catalog(root).All.Select(p => p.Manifest.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
