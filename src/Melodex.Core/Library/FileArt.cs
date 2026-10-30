namespace Melodex.Core.Library;

/// <summary>A music file's own picture: the cover embedded in it, or else an image beside it such as folder.jpg.</summary>
public static class FileArt
{
    /// <summary>Image names a folder's cover goes by, best first, without their extension.</summary>
    private static readonly string[] Names = ["cover", "folder", "front", "album", "albumart", "albumartsmall"];

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    public sealed record Picture(byte[] Data, string ContentType);

    /// <summary>The embedded front cover, any embedded picture, then an image in the folder; null if there's none.</summary>
    public static Picture? Find(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures ?? [];
            var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures.FirstOrDefault();
            if (picture is { Data.Count: > 0 })
                return new Picture(picture.Data.Data, ContentType(picture.MimeType, picture.Data.Data));
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            // Unreadable tags: the folder may still have a picture.
        }
        return Beside(path) is { } image ? new Picture(File.ReadAllBytes(image), ContentType(null, null, image)) : null;
    }

    /// <summary>The folder's cover image: one with a cover's usual name, or the only image there.</summary>
    public static string? Beside(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            return null;
        var images = Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var name in Names)
            if (images.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } named)
                return named;
        return images.Count == 1 ? images[0] : null;
    }

    private static string ContentType(string? declared, byte[]? data, string? path = null)
    {
        if (declared is { Length: > 0 } && declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return declared.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : declared;
        if (data is { Length: > 3 })
        {
            if (data[0] == 0x89 && data[1] == 0x50) return "image/png";
            if (data[0] == 0xFF && data[1] == 0xD8) return "image/jpeg";
        }
        return Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
    }
}
