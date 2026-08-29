using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odyssey.Infrastructure;

namespace Odyssey.Desktop;

public enum UpdateCheckStatus { UpToDate, Ready, Unsupported }

public sealed record PreparedUpdate(
    Version Version,
    string ArchivePath,
    string ArchiveFormat,
    string ReleaseNotesUrl,
    string Sha256);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, Version CurrentVersion, PreparedUpdate? Update = null);

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/t-vanek/Odyssey/releases/latest";
    private const string TrustedReleasePrefix = "/t-vanek/Odyssey/releases/download/";
    private readonly HttpClient _http;
    private readonly string _updatesDirectory;
    private readonly string _installDirectory;
    private readonly string _processPath;
    private readonly string? _platformKey;
    private PreparedUpdate? _prepared;

    public UpdateService(HttpClient http, ApplicationStorage storage)
        : this(http, storage, ResolveCurrentVersion(), ResolvePlatformKey(), AppContext.BaseDirectory,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Odyssey.Desktop.exe" : "Odyssey.Desktop"))
    {
    }

    public UpdateService(
        HttpClient http,
        ApplicationStorage storage,
        Version currentVersion,
        string? platformKey,
        string installDirectory,
        string processPath)
    {
        _http = http;
        CurrentVersion = currentVersion;
        _platformKey = platformKey;
        _installDirectory = Path.GetFullPath(installDirectory);
        _processPath = Path.GetFullPath(processPath);
        _updatesDirectory = Path.Combine(storage.DirectoryPath, "updates");
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionText => FormatVersion(CurrentVersion);
    public bool CanInstall => _platformKey is not null && File.Exists(UpdaterPath);
    public PreparedUpdate? Prepared => _prepared;

    public async Task<UpdateCheckResult> CheckAndPrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_platformKey is null) return new UpdateCheckResult(UpdateCheckStatus.Unsupported, CurrentVersion);

        var release = await GetJsonAsync<GitHubRelease>(LatestReleaseApi, cancellationToken)
                      ?? throw new InvalidDataException("GitHub returned an empty release response.");
        var manifestAsset = release.Assets.FirstOrDefault(asset => asset.Name == "update-manifest.json")
                            ?? throw new InvalidDataException("The latest release does not contain update-manifest.json.");
        ValidateReleaseUrl(manifestAsset.DownloadUrl);

        var manifest = await GetJsonAsync<UpdateManifest>(manifestAsset.DownloadUrl, cancellationToken)
                       ?? throw new InvalidDataException("The update manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported update manifest version.");
        if (!Version.TryParse(manifest.Version, out var availableVersion))
            throw new InvalidDataException("The update manifest contains an invalid version.");
        if (availableVersion <= CurrentVersion)
        {
            _prepared = null;
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, CurrentVersion);
        }

        if (!manifest.Assets.TryGetValue(_platformKey, out var asset))
            return new UpdateCheckResult(UpdateCheckStatus.Unsupported, CurrentVersion);
        ValidateManifestAsset(asset);

        var versionDirectory = Path.Combine(_updatesDirectory, FormatVersion(availableVersion));
        Directory.CreateDirectory(versionDirectory);
        var archivePath = Path.Combine(versionDirectory, asset.FileName);
        if (!File.Exists(archivePath) || !await HasExpectedHashAsync(archivePath, asset.Sha256, cancellationToken))
            await DownloadVerifiedAsync(asset.DownloadUrl, archivePath, asset.Sha256, cancellationToken);

        _prepared = new PreparedUpdate(availableVersion, archivePath, asset.Format, manifest.ReleaseNotesUrl, asset.Sha256);
        return new UpdateCheckResult(UpdateCheckStatus.Ready, CurrentVersion, _prepared);
    }

    public async Task LaunchPreparedInstallerAsync(CancellationToken cancellationToken = default)
    {
        var update = _prepared ?? throw new InvalidOperationException("No verified update is ready.");
        if (!CanInstall) throw new InvalidOperationException("This build does not contain the update helper.");
        if (!await HasExpectedHashAsync(update.ArchivePath, update.Sha256, cancellationToken))
            throw new CryptographicException("The downloaded update no longer matches its SHA-256 checksum.");
        VerifyInstallDirectoryWritable();

        var runnerDirectory = Path.Combine(_updatesDirectory, "runner");
        Directory.CreateDirectory(runnerDirectory);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var runnerPath = Path.Combine(runnerDirectory, $"Odyssey.Updater-{Guid.NewGuid():N}{extension}");
        File.Copy(UpdaterPath, runnerPath, overwrite: false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runnerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var logPath = Path.Combine(Path.GetDirectoryName(update.ArchivePath)!, "update.log");
        var start = new ProcessStartInfo(runnerPath) { UseShellExecute = false, WorkingDirectory = runnerDirectory };
        AddArgument(start, "--wait-pid", Environment.ProcessId.ToString());
        AddArgument(start, "--archive", update.ArchivePath);
        AddArgument(start, "--install-dir", _installDirectory);
        AddArgument(start, "--executable", Path.GetFileName(_processPath));
        AddArgument(start, "--format", update.ArchiveFormat);
        AddArgument(start, "--log", logPath);
        _ = Process.Start(start) ?? throw new InvalidOperationException("The update helper could not be started.");
    }

    private string UpdaterPath => Path.Combine(_installDirectory, OperatingSystem.IsWindows() ? "Odyssey.Updater.exe" : "Odyssey.Updater");

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private async Task DownloadVerifiedAsync(string url, string destination, string expectedHash, CancellationToken cancellationToken)
    {
        ValidateReleaseUrl(url);
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, 1024 * 128, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }
            if (!await HasExpectedHashAsync(temporary, expectedHash, cancellationToken))
                throw new CryptographicException("Downloaded update failed SHA-256 verification.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { throw new InvalidDataException("The update manifest contains an invalid SHA-256 checksum."); }
        if (expected.Length != 32) throw new InvalidDataException("The update manifest contains an invalid SHA-256 checksum.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void ValidateManifestAsset(UpdateAsset asset)
    {
        if (asset.FileName != Path.GetFileName(asset.FileName) || string.IsNullOrWhiteSpace(asset.FileName))
            throw new InvalidDataException("The update manifest contains an invalid file name.");
        if (asset.Format is not ("zip" or "tar.gz"))
            throw new InvalidDataException("The update manifest contains an unsupported archive format.");
        ValidateReleaseUrl(asset.DownloadUrl);
        _ = Convert.FromHexString(asset.Sha256);
    }

    private static void ValidateReleaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(TrustedReleasePrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The update manifest points outside the official Odyssey releases.");
    }

    private void VerifyInstallDirectoryWritable()
    {
        var testPath = Path.Combine(_installDirectory, $".odyssey-update-write-{Guid.NewGuid():N}");
        try { File.WriteAllText(testPath, string.Empty); }
        finally { try { if (File.Exists(testPath)) File.Delete(testPath); } catch { } }
    }

    private static void AddArgument(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    private static Version ResolveCurrentVersion()
    {
        var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var normalized = informational?.Split('+', 2)[0].Split('-', 2)[0];
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0, 0);
    }

    private static string FormatVersion(Version version) =>
        version.ToString(version.Revision >= 0 ? 4 : version.Build >= 0 ? 3 : 2);

    private static string? ResolvePlatformKey()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return null;
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsLinux()) return "linux-x64";
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private sealed record GitHubRelease([property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
    private sealed record UpdateManifest(int SchemaVersion, string Version, string Tag, string PublishedAt, string ReleaseNotesUrl, IReadOnlyDictionary<string, UpdateAsset> Assets);
    private sealed record UpdateAsset(string FileName, string DownloadUrl, string Sha256, string Format);
}
