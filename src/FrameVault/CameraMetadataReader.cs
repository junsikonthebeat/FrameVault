using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using ImageMetadataReader = MetadataExtractor.ImageMetadataReader;

namespace FrameVault;

public sealed record CameraMetadataSummary(
    string FileName,
    string CaptureDate,
    string ShutterCount,
    string CameraModel,
    string SerialNumber,
    string BodyManufactureDate,
    string LensMake,
    string LensModel,
    string LensInfo);

public static class CameraMetadataReader
{
    private sealed record MetadataTagValue(string Directory, string Name, string Value);

    public static CameraMetadataSummary Read(Stream stream, PhotoCandidate source)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try { directories = ImageMetadataReader.ReadMetadata(stream); }
        catch
        {
            return Empty(source);
        }

        var tags = directories
            .SelectMany(directory => directory.Tags.Select(tag =>
                new MetadataTagValue(directory.Name, tag.Name, Clean(tag.Description))))
            .Where(tag => tag.Value.Length > 0)
            .ToList();

        var captureDate = ReadCaptureDate(directories)?.ToString("yyyy-MM-dd HH:mm:ss")
            ?? source.TakenAt.ToString("yyyy-MM-dd HH:mm:ss");

        return new(
            source.FileName,
            captureDate,
            ReadShutterCount(directories, tags),
            Find(tags, ["Model", "Camera Model Name"], excludeLens: true),
            Find(tags, ["Body Serial Number", "Camera Serial Number", "Serial Number"], excludeLens: true),
            Find(tags, ["Camera Manufacture Date", "Body Manufacture Date", "Manufacturing Date", "Manufacture Date", "Production Date"], excludeLens: true),
            Find(tags, ["Lens Make"]),
            Find(tags, ["Lens Model", "Lens Type", "Lens"], requireLens: true),
            Find(tags, ["Lens Specification", "Lens Info"], requireLens: true));
    }

    private static string ReadShutterCount(
        IReadOnlyList<MetadataExtractor.Directory> directories,
        IReadOnlyList<MetadataTagValue> tags)
    {
        // MetadataExtractor 2.8.x labels Nikon MakerNote 0x00A7 as
        // "Exposure Sequence Number", but Nikon defines it as the total shutter count.
        // Restrict the numeric lookup to Nikon's MakerNote directory so an unrelated
        // sequence number from another manufacturer cannot be mistaken for a count.
        foreach (var directory in directories.OfType<NikonType2MakernoteDirectory>())
        {
            var value = directory.GetObject(0x00A7);
            if (value is uint unsigned) return unsigned.ToString();
            if (value is int signed && signed >= 0) return signed.ToString();
            if (value is not null && ulong.TryParse(value.ToString(), out var parsed))
                return parsed.ToString();
        }

        return Find(tags,
            ["Shutter Count", "Shutter Releases", "Total Shutter Releases", "Image Count", "Actuation Count"]);
    }
    private static CameraMetadataSummary Empty(PhotoCandidate source) => new(
        source.FileName,
        source.TakenAt.ToString("yyyy-MM-dd HH:mm:ss"),
        "N/A", "N/A", "N/A", "N/A", "N/A", "N/A", "N/A");

    private static DateTime? ReadCaptureDate(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var directory in directories.OfType<ExifSubIfdDirectory>())
            if (MetadataExtractor.DirectoryExtensions.TryGetDateTime(
                directory, ExifDirectoryBase.TagDateTimeOriginal, out var value))
                return value;
        return null;
    }

    private static string Find(
        IReadOnlyList<MetadataTagValue> tags,
        IReadOnlyList<string> names,
        bool excludeLens = false,
        bool requireLens = false)
    {
        foreach (var requested in names)
        {
            var normalized = Normalize(requested);
            var match = tags.FirstOrDefault(tag =>
                Normalize(tag.Name) == normalized &&
                (!excludeLens || !ContainsLens(tag)) &&
                (!requireLens || ContainsLens(tag)));
            if (match is not null) return match.Value;
        }

        foreach (var requested in names)
        {
            var normalized = Normalize(requested);
            var match = tags.FirstOrDefault(tag =>
                Normalize(tag.Name).Contains(normalized, StringComparison.Ordinal) &&
                (!excludeLens || !ContainsLens(tag)) &&
                (!requireLens || ContainsLens(tag)));
            if (match is not null) return match.Value;
        }
        return "N/A";
    }

    private static bool ContainsLens(MetadataTagValue tag) =>
        tag.Name.Contains("Lens", StringComparison.OrdinalIgnoreCase) ||
        tag.Directory.Contains("Lens", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace("\0", "");
}


