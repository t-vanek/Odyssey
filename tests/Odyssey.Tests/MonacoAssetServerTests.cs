using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Odyssey.Desktop;

namespace Odyssey.Tests;

public sealed class MonacoAssetServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-monaco-assets-{Guid.NewGuid():N}");

    [Fact]
    public async Task BundledAssets_AreIntegrityCheckedAndServedOnlyBehindAnOpaqueLoopbackRoute()
    {
        await using var server = new MonacoAssetServer();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.EditorUri);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IPAddress.Loopback.ToString(), server.EditorUri.Host);
        Assert.Equal(64, server.EditorUri.Segments[1].TrimEnd('/').Length);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var missing = new Uri(server.EditorUri, "missing.js");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(missing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(new Uri($"http://127.0.0.1:{server.EditorUri.Port}/wrong/index.html"))).StatusCode);
    }

    [Fact]
    public void ChangedAsset_IsRejectedBeforeTheServerStarts()
    {
        Directory.CreateDirectory(_root);
        WriteAsset("index.html", "<html></html>");
        WriteAsset("editor.js", "globalThis.odysseyEditor = {};");
        WriteManifest();
        File.AppendAllText(Path.Combine(_root, "editor.js"), "// tampered");

        var exception = Assert.Throws<InvalidDataException>(() => new MonacoAssetServer(_root));

        Assert.Contains("missing or changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TraversalEntryInManifest_IsRejected()
    {
        Directory.CreateDirectory(_root);
        WriteAsset("index.html", "<html></html>");
        WriteAsset("editor.js", "globalThis.odysseyEditor = {};");
        var manifest = new
        {
            schemaVersion = 1,
            assets = new Dictionary<string, object>
            {
                ["index.html"] = Entry("index.html"),
                ["editor.js"] = Entry("editor.js"),
                ["../escape.js"] = new { bytes = 1, sha256 = new string('A', 64) }
            }
        };
        File.WriteAllText(Path.Combine(_root, "asset-manifest.json"), JsonSerializer.Serialize(manifest));

        Assert.Throws<InvalidDataException>(() => new MonacoAssetServer(_root));
    }

    [Fact]
    public async Task Assets_AreServedFromTheVerifiedSnapshotAfterStartup()
    {
        Directory.CreateDirectory(_root);
        WriteAsset("index.html", "<html>trusted</html>");
        WriteAsset("editor.js", "globalThis.odysseyEditor = {};");
        WriteManifest();
        await using var server = new MonacoAssetServer(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<html>changed later</html>");

        using var client = new HttpClient();
        var content = await client.GetStringAsync(server.EditorUri);

        Assert.Equal("<html>trusted</html>", content);
    }

    private void WriteAsset(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, name), content);

    private void WriteManifest()
    {
        var manifest = new
        {
            schemaVersion = 1,
            assets = new Dictionary<string, object>
            {
                ["index.html"] = Entry("index.html"),
                ["editor.js"] = Entry("editor.js")
            }
        };
        File.WriteAllText(Path.Combine(_root, "asset-manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private object Entry(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(_root, name));
        return new { bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
