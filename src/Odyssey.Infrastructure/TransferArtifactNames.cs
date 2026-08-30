namespace Odyssey.Infrastructure;

internal static class TransferArtifactNames
{
    private const string PartMarker = ".odyssey-part-";
    private const string BackupMarker = ".odyssey-backup-";

    public static bool IsInternal(string name) =>
        TryGetGuidSuffix(name, PartMarker, out _) || TryGetGuidSuffix(name, BackupMarker, out _);

    public static bool TryGetBackupId(string name, out Guid id) =>
        TryGetGuidSuffix(name, BackupMarker, out id);

    private static bool TryGetGuidSuffix(string name, string marker, out Guid id)
    {
        var markerIndex = name.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            id = Guid.Empty;
            return false;
        }
        var value = name[(markerIndex + marker.Length)..];
        return Guid.TryParseExact(value, "N", out id);
    }
}
