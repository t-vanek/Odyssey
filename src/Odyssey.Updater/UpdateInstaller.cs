using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace Odyssey.Updater;

public sealed record UpdateInstallerOptions(
    int WaitForProcessId,
    string ArchivePath,
    string InstallDirectory,
    string ExecutableName,
    string ArchiveFormat,
    string LogPath)
{
    public static UpdateInstallerOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Updater arguments must use --name value pairs.");
            values[args[index][2..]] = args[index + 1];
        }

        if (!int.TryParse(Required(values, "wait-pid"), out var processId) || processId <= 0)
            throw new ArgumentException("Invalid process identifier.");

        var archive = Path.GetFullPath(Required(values, "archive"));
        var installDirectory = Path.GetFullPath(Required(values, "install-dir"));
        var executable = Required(values, "executable");
        var format = Required(values, "format");
        var log = Path.GetFullPath(values.GetValueOrDefault("log") ?? Path.Combine(Path.GetDirectoryName(archive)!, "update.log"));

        if (executable != Path.GetFileName(executable)) throw new ArgumentException("Executable must be a file name.");
        if (format is not ("zip" or "tar.gz")) throw new ArgumentException("Unsupported archive format.");
        if (!File.Exists(archive)) throw new FileNotFoundException("Update archive was not found.", archive);
        if (!Directory.Exists(installDirectory)) throw new DirectoryNotFoundException(installDirectory);
        return new UpdateInstallerOptions(processId, archive, installDirectory, executable, format, log);
    }

    public static string TryResolveLogPath(IReadOnlyList<string> args)
    {
        for (var index = 0; index + 1 < args.Count; index += 2)
            if (args[index] == "--log") return Path.GetFullPath(args[index + 1]);
        return Path.Combine(Path.GetTempPath(), "odyssey-update.log");
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");
}

public sealed class UpdateInstaller(Action<string>? log = null, Action<string, string>? restart = null)
{
    public async Task InstallAsync(UpdateInstallerOptions options, CancellationToken cancellationToken = default)
    {
        var workRoot = Path.Combine(Path.GetDirectoryName(options.ArchivePath)!, $"apply-{Guid.NewGuid():N}");
        var extracted = Path.Combine(workRoot, "new");
        var backup = Path.Combine(workRoot, "backup");
        var createdFiles = new List<string>();
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(backup);

        try
        {
            Log("Waiting for Odyssey to exit.");
            await WaitForExitAsync(options.WaitForProcessId, cancellationToken);
            Log("Extracting verified update archive.");
            Extract(options.ArchivePath, options.ArchiveFormat, extracted);
            ValidateExtractedTree(extracted);
            if (!File.Exists(Path.Combine(extracted, options.ExecutableName)))
                throw new InvalidDataException("The update does not contain the Odyssey executable.");

            ApplyWithRollback(extracted, options.InstallDirectory, backup, createdFiles);
            Log("Update installed successfully.");
            if (restart is null) Restart(options.InstallDirectory, options.ExecutableName);
            else restart(options.InstallDirectory, options.ExecutableName);
        }
        catch
        {
            Log("Update failed. Restoring overwritten files.");
            RestoreBackup(backup, options.InstallDirectory);
            RemoveCreatedFiles(createdFiles, options.InstallDirectory);
            throw;
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { }
        }
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }

    private static void Extract(string archivePath, string format, string destination)
    {
        if (format == "zip")
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: false);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    private static void ValidateExtractedTree(string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
                throw new InvalidDataException("The update archive contains an invalid path.");
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update archive contains an unsupported symbolic link.");
        }
    }

    private static void ApplyWithRollback(string sourceRoot, string installRoot, string backupRoot, ICollection<string> createdFiles)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = SafeCombine(installRoot, relative);
            var backup = SafeCombine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
            }
            else
            {
                createdFiles.Add(target);
            }
            File.Copy(source, target, overwrite: true);
            CopyUnixMode(source, target);
        }
    }

    private static void RemoveCreatedFiles(IEnumerable<string> createdFiles, string installRoot)
    {
        foreach (var path in createdFiles.Reverse())
        {
            var relative = Path.GetRelativePath(installRoot, path);
            var target = SafeCombine(installRoot, relative);
            try { if (File.Exists(target)) File.Delete(target); } catch { }
        }
    }

    private static void RestoreBackup(string backupRoot, string installRoot)
    {
        if (!Directory.Exists(backupRoot)) return;
        foreach (var backup in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backupRoot, backup);
            var target = SafeCombine(installRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backup, target, overwrite: true);
            CopyUnixMode(backup, target);
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!combined.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw new InvalidDataException("An update file escapes the installation directory.");
        return combined;
    }

    private static void CopyUnixMode(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(destination, File.GetUnixFileMode(source)); } catch { }
    }

    private static void Restart(string installDirectory, string executableName)
    {
        var executable = Path.Combine(installDirectory, executableName);
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = installDirectory, UseShellExecute = true });
    }

    private void Log(string message) => log?.Invoke(message);
}
