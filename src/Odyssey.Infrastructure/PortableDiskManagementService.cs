using System.ComponentModel;
using System.Diagnostics;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class PortableDiskManagementService : IDiskManagementService
{
    public Task UnmountAsync(StorageVolume volume, CancellationToken cancellationToken = default) =>
        RunAsync(volume, eject: false, cancellationToken);

    public Task EjectAsync(StorageVolume volume, CancellationToken cancellationToken = default) =>
        RunAsync(volume, eject: true, cancellationToken);

    private static async Task RunAsync(StorageVolume volume, bool eject, CancellationToken cancellationToken)
    {
        if (!volume.CanUnmount || string.IsNullOrWhiteSpace(volume.DevicePath))
            throw new InvalidOperationException("This location cannot be safely disconnected by Odyssey.");

        if (OperatingSystem.IsLinux())
        {
            await RunProcessAsync("udisksctl", ["unmount", "-b", volume.DevicePath], cancellationToken);
            if (eject && volume.IsRemovable)
                await RunProcessAsync("udisksctl", ["power-off", "-b", volume.DevicePath], cancellationToken);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            await RunProcessAsync("mountvol", [volume.RootPath, "/p"], cancellationToken);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            await RunProcessAsync("diskutil", [eject ? "eject" : "unmount", volume.DevicePath], cancellationToken);
            return;
        }

        throw new PlatformNotSupportedException("Disk disconnection is not supported on this operating system.");
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0) return;
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new IOException(string.IsNullOrWhiteSpace(error)
                ? $"{fileName} exited with code {process.ExitCode}."
                : error.Trim());
        }
        catch (Win32Exception ex)
        {
            throw new PlatformNotSupportedException($"Required system tool '{fileName}' is not available.", ex);
        }
    }
}
