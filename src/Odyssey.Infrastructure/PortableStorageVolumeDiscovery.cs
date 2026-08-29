using System.Runtime.InteropServices;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class PortableStorageVolumeDiscovery : IStorageVolumeDiscovery
{
    private static readonly HashSet<string> LinuxFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "btrfs", "ext2", "ext3", "ext4", "xfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "iso9660", "udf"
    };

    public Task<IReadOnlyList<StorageVolume>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StorageVolume> result = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? DiscoverWindows()
            : DiscoverLinux();
        return Task.FromResult(result);
    }

    private static IReadOnlyList<StorageVolume> DiscoverWindows()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var volumes = new List<StorageVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                volumes.Add(new StorageVolume(
                    drive.RootDirectory.FullName, name, drive.DriveType == DriveType.Removable,
                    drive.RootDirectory.FullName, CanUnmount: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive can disappear between enumeration and metadata access.
            }
        }
        return volumes;
    }

    private static IReadOnlyList<StorageVolume> DiscoverLinux()
    {
        const string mountInfoPath = "/proc/self/mountinfo";
        if (!File.Exists(mountInfoPath)) return [];
        var volumes = new Dictionary<string, StorageVolume>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(mountInfoPath))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");
            if (separator < 0 || fields.Length <= separator + 2 || fields.Length < 5) continue;
            var mountPoint = DecodeMountPath(fields[4]);
            var fileSystem = fields[separator + 1];
            var source = DecodeMountPath(fields[separator + 2]);
            if (!source.StartsWith("/dev/", StringComparison.Ordinal) || !LinuxFileSystems.Contains(fileSystem)) continue;
            if (IsSystemMount(mountPoint) || !Directory.Exists(mountPoint)) continue;

            var name = Path.GetFileName(mountPoint.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = source;
            var removable = mountPoint.StartsWith("/run/media/", StringComparison.Ordinal)
                            || mountPoint.StartsWith("/media/", StringComparison.Ordinal)
                            || mountPoint.StartsWith("/mnt/", StringComparison.Ordinal);
            volumes[mountPoint] = new StorageVolume(mountPoint, name, removable, source, CanUnmount: true);
        }
        return volumes.Values.OrderBy(volume => volume.RootPath, StringComparer.Ordinal).ToArray();
    }

    private static bool IsSystemMount(string path) => path is "/" or "/home" or "/boot" or "/boot/efi";

    private static string DecodeMountPath(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
