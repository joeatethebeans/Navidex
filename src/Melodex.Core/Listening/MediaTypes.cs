namespace Melodex.Core.Listening;

/// <summary>Media types for audio files, so players can check they can play a file before trying.</summary>
public static class MediaTypes
{
    /// <summary>The usual codec for a file extension, when nothing better is known.</summary>
    public static string? CodecFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".opus" => "opus",
        ".ogg" or ".oga" => "vorbis",
        ".mp3" => "mp3",
        ".flac" => "flac",
        ".m4a" or ".mp4" or ".aac" => "aac",
        ".webm" => "opus",
        ".wav" => "pcm",
        ".wma" => "wma",
        _ => null,
    };

    /// <summary>The container, from the extension: <c>ogg</c>, <c>mp4</c>, <c>flac</c> and so on.</summary>
    public static string? ContainerFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".opus" or ".ogg" or ".oga" => "ogg",
        ".m4a" or ".mp4" or ".aac" => "mp4",
        ".mp3" => "mp3",
        ".flac" => "flac",
        ".webm" => "webm",
        ".wav" => "wav",
        ".wma" => "asf",
        _ => null,
    };

    /// <summary>
    /// The media type to serve a file as, with the codec when the container can hold several, such as
    /// <c>audio/ogg; codecs=opus</c>. Browsers answer <c>canPlayType</c> for exactly this string.
    /// </summary>
    public static string For(string path, string? codec = null)
    {
        codec ??= CodecFor(path);
        return ContainerFor(path) switch
        {
            "ogg" => codec is null ? "audio/ogg" : $"audio/ogg; codecs={codec}",
            "webm" => codec is null ? "audio/webm" : $"audio/webm; codecs={codec}",
            "mp4" => codec switch
            {
                "alac" => "audio/mp4; codecs=alac",
                "aac" or null => "audio/mp4; codecs=mp4a.40.2",
                _ => $"audio/mp4; codecs={codec}",
            },
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "asf" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }

    /// <summary>The media type without parameters, for the <c>Content-Type</c> header.</summary>
    public static string Bare(string mediaType) => mediaType.Split(';')[0];

    /// <summary>The codec and bitrate as the file says, for when the plugin didn't.</summary>
    public static (string? Codec, int? BitrateKbps) Probe(string fullPath)
    {
        try
        {
            using var file = TagLib.File.Create(fullPath);
            var description = file.Properties?.Codecs.OfType<TagLib.IAudioCodec>().FirstOrDefault()?.Description ?? "";
            string? codec = description.Contains("ALAC", StringComparison.OrdinalIgnoreCase) ? "alac"
                : description.Contains("Opus", StringComparison.OrdinalIgnoreCase) ? "opus"
                : description.Contains("Vorbis", StringComparison.OrdinalIgnoreCase) ? "vorbis"
                : description.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "flac"
                : description.Contains("Layer 3", StringComparison.OrdinalIgnoreCase) ? "mp3"
                : description.Contains("AAC", StringComparison.OrdinalIgnoreCase) ? "aac"
                : null;
            return (codec, file.Properties?.AudioBitrate is > 0 and var bitrate ? bitrate : null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, null);
        }
    }
}
