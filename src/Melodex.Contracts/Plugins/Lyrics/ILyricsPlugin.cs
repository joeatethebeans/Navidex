using Melodex.Contracts.Catalogue;

namespace Melodex.Contracts.Plugins.Lyrics;

/// <summary>
/// Lyrics plugin contract (version 1.0): the words of songs, time-synced where the source has them. The core asks the
/// enabled lyrics plugins in the user's order for each song in the library without lyrics, keeps the first answer, and
/// writes it beside or into the song's files as the settings say.
/// </summary>
/// <remarks>
/// Return null when the plugin has no lyrics for the song; throw for anything else, such as network failures, so the
/// core tries again later instead of remembering that there are none.
/// </remarks>
public interface ILyricsPlugin : IPlugin
{
    /// <summary>The song's lyrics, or null if the plugin has none.</summary>
    Task<FoundLyrics?> FindAsync(LyricsQuery query, CancellationToken cancellationToken);
}

/// <summary>The song to find lyrics for, with everything the core knows that might identify it.</summary>
/// <param name="Title">The song's title.</param>
/// <param name="Artist">Its artist credit.</param>
/// <param name="Album">The album the file in the library is from, if it's matched to one.</param>
/// <param name="Duration">The length of the file in the library, to tell versions apart.</param>
/// <param name="Ids">The recording's IDs, such as its MusicBrainz recording ID and ISRCs.</param>
public sealed record LyricsQuery(string Title, string Artist, string? Album, TimeSpan? Duration, IReadOnlyList<ProviderId> Ids);

/// <summary>A song's lyrics.</summary>
/// <param name="Synced">The lyrics in LRC format, each line with the time it's sung, such as <c>[01:02.50] words</c>.</param>
/// <param name="Plain">The lyrics as plain text, one line per line.</param>
/// <param name="Instrumental">The song has no words. Then both are null.</param>
public sealed record FoundLyrics(string? Synced, string? Plain, bool Instrumental = false);

/// <summary>Capability names a lyrics plugin declares.</summary>
public static class LyricsCapabilities
{
    /// <summary>Time-synced lyrics, for following along.</summary>
    public const string Synced = "synced";

    /// <summary>Plain lyrics.</summary>
    public const string Plain = "plain";
}
