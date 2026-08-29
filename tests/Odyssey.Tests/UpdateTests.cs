using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Odyssey.Desktop;
using Odyssey.Infrastructure;
using Odyssey.Updater;

namespace Odyssey.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-update-{Guid.NewGuid():N}");

    [Fact]
    public async Task NewRelease_IsDownloadedAndVerifiedBeforeItBecomesReady()
    {
        var package = Encoding.UTF8.GetBytes("verified Odyssey package");
        var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        using var http = CreateClient("2.1.0", hash, package);
        var service = CreateService(http);

        var result = await service.CheckAndPrepareAsync();

        Assert.Equal(UpdateCheckStatus.Ready, result.Status);
        Assert.Equal(new Version(2, 1, 0), result.Update!.Version);
        Assert.Equal(package, await File.ReadAllBytesAsync(result.Update.ArchivePath));
    }

    [Fact]
    public async Task CorruptedRelease_IsRejectedBySha256()
    {
        using var http = CreateClient("2.0.0", new string('0', 64), Encoding.UTF8.GetBytes("tampered"));
        var service = CreateService(http);

        await Assert.ThrowsAsync<CryptographicException>(() => service.CheckAndPrepareAsync());
    }

    [Fact]
    public async Task CurrentRelease_DoesNotDownloadAPlatformPackage()
    {
        using var http = CreateClient("1.0.0", new string('0', 64), Encoding.UTF8.GetBytes("unused"));
        var service = CreateService(http);

        var result = await service.CheckAndPrepareAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task UpdateHelper_ReplacesPackagedFilesFromVerifiedArchive()
    {
        var install = Path.Combine(_root, "install");
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(install, "Odyssey.Desktop"), "old");
        await File.WriteAllTextAsync(Path.Combine(payload, "Odyssey.Desktop"), "new");
        await File.WriteAllTextAsync(Path.Combine(payload, "component.dll"), "component");
        var archive = Path.Combine(_root, "update.zip");
        ZipFile.CreateFromDirectory(payload, archive);
        var restarted = false;
        var installer = new UpdateInstaller(restart: (_, executable) => restarted = executable == "Odyssey.Desktop");
        var options = new UpdateInstallerOptions(int.MaxValue, archive, install, "Odyssey.Desktop", "zip", Path.Combine(_root, "update.log"));

        await installer.InstallAsync(options);

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(install, "Odyssey.Desktop")));
        Assert.Equal("component", await File.ReadAllTextAsync(Path.Combine(install, "component.dll")));
        Assert.True(restarted);
    }

    [Fact]
    public async Task UpdateHelper_InstallsLinuxTarGzipPackage()
    {
        var install = Path.Combine(_root, "tar-install");
        var payload = Path.Combine(_root, "tar-payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(install, "Odyssey.Desktop"), "old");
        await File.WriteAllTextAsync(Path.Combine(payload, "Odyssey.Desktop"), "new");
        var archive = Path.Combine(_root, "update.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            TarFile.CreateFromDirectory(payload, gzip, includeBaseDirectory: false);
        var installer = new UpdateInstaller(restart: (_, _) => { });
        var options = new UpdateInstallerOptions(int.MaxValue, archive, install, "Odyssey.Desktop", "tar.gz", Path.Combine(_root, "tar-update.log"));

        await installer.InstallAsync(options);

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(install, "Odyssey.Desktop")));
    }

    private UpdateService CreateService(HttpClient http)
    {
        var storage = new ApplicationStorage(Path.Combine(_root, "data"));
        var install = Path.Combine(_root, "app");
        Directory.CreateDirectory(install);
        return new UpdateService(http, storage, new Version(1, 0, 0), "linux-x64", install, Path.Combine(install, "Odyssey.Desktop"));
    }

    private static HttpClient CreateClient(string version, string hash, byte[] package)
    {
        const string manifestUrl = "https://github.com/t-vanek/Odyssey/releases/download/v2.0.0/update-manifest.json";
        const string packageUrl = "https://github.com/t-vanek/Odyssey/releases/download/v2.0.0/Odyssey-linux-x64.tar.gz";
        var latest = $$"""
            { "assets": [{ "name": "update-manifest.json", "browser_download_url": "{{manifestUrl}}" }] }
            """;
        var manifest = $$"""
            {
              "schemaVersion": 1,
              "version": "{{version}}",
              "tag": "v2.0.0",
              "publishedAt": "2026-08-29T00:00:00Z",
              "releaseNotesUrl": "https://github.com/t-vanek/Odyssey/releases/tag/v2.0.0",
              "assets": {
                "linux-x64": {
                  "fileName": "Odyssey-linux-x64.tar.gz",
                  "downloadUrl": "{{packageUrl}}",
                  "sha256": "{{hash}}",
                  "format": "tar.gz"
                }
              }
            }
            """;
        return new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/t-vanek/Odyssey/releases/latest"] = Encoding.UTF8.GetBytes(latest),
            [manifestUrl] = Encoding.UTF8.GetBytes(manifest),
            [packageUrl] = package
        }));
    }

    private sealed class StaticHttpHandler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && responses.TryGetValue(request.RequestUri.AbsoluteUri, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
