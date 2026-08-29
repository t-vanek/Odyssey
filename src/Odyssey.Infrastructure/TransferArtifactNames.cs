namespace Odyssey.Infrastructure;

internal static class TransferArtifactNames
{
    private const string PartMarker = ".odyssey-part-";
    private const string BackupMarker = ".odyssey-backup-";

    public static bool IsInternal(string name) =>
        HasGuidSuffix(name, PartMarker) || HasGuidSuffix(name, BackupMarker);

    private static bool HasGuidSuffix(string name, string marker)
    {
        var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return false;
        var value = name[(markerIndex + marker.Length)..];
        return Guid.TryParseExact(value, "N", out _);
    }
}
