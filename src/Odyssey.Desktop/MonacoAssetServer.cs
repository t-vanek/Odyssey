using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Odyssey.Desktop;

internal sealed class MonacoAssetServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const long MaximumAssetBytes = 16 * 1024 * 1024;
    private const long MaximumBundleBytes = 32 * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, byte[]> _assets;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly string _routeToken;

    public MonacoAssetServer(string? assetRoot = null)
    {
        var root = Path.GetFullPath(assetRoot ?? Path.Combine(AppContext.BaseDirectory, "Monaco", "dist"));
        _assets = LoadAndValidateManifest(root);
        _routeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        EditorUri = new Uri($"http://127.0.0.1:{endpoint.Port}/{_routeToken}/index.html");
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public Uri EditorUri { get; }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = ServeAsync(client, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            try
            {
                var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                if (header is null)
                {
                    await WriteStatusAsync(stream, 431, "Request Header Fields Too Large", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = firstLineEnd < 0 ? header : header[..firstLineEnd];
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0] is not ("GET" or "HEAD")
                                      || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                {
                    await WriteStatusAsync(stream, 405, "Method Not Allowed", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                var prefix = $"/{_routeToken}/";
                if (!parts[1].StartsWith(prefix, StringComparison.Ordinal)
                    || parts[1].Length <= prefix.Length
                    || parts[1].Contains('?')
                    || parts[1].Contains('#'))
                {
                    await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }
                var name = parts[1][prefix.Length..];
                if (!_assets.TryGetValue(name, out var asset))
                {
                    await WriteStatusAsync(stream, 404, "Not Found", cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WriteAssetAsync(
                    stream, name, asset, parts[0] == "HEAD", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private async Task WriteAssetAsync(
        NetworkStream response,
        string name,
        byte[] content,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {ContentType(name)}\r\n" +
            $"Content-Length: {content.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Cross-Origin-Resource-Policy: same-origin\r\n" +
            "Connection: close\r\n\r\n");
        await response.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (headOnly) return;
        await response.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (length >= 4 && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                return Encoding.ASCII.GetString(buffer, 0, length);
        }
        return null;
    }

    private static Task WriteStatusAsync(
        NetworkStream response,
        int status,
        string reason,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes($"{status} {reason}\n");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        return WriteResponseAsync(response, header, body, cancellationToken);
    }

    private static async Task WriteResponseAsync(
        Stream response,
        byte[] header,
        byte[] body,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await response.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, byte[]> LoadAndValidateManifest(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The bundled Monaco editor assets are unavailable.");
        var manifestPath = ResolveAssetPath(root, "asset-manifest.json");
        var manifestInfo = new FileInfo(manifestPath);
        manifestInfo.Refresh();
        if (!manifestInfo.Exists || manifestInfo.LinkTarget is not null || manifestInfo.Length > 1024 * 1024)
            throw new InvalidDataException("The Monaco asset manifest is unavailable or unsafe.");
        var manifest = JsonSerializer.Deserialize<AssetManifest>(File.ReadAllText(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException("The Monaco asset manifest is invalid.");
        if (manifest.SchemaVersion != 1 || manifest.Assets.Count == 0)
            throw new InvalidDataException("The Monaco asset manifest version is not supported.");
        var verified = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (name, expected) in manifest.Assets)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/')
                || name.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("The Monaco asset manifest contains an unsafe path.");
            if (expected.Bytes is < 0 or > MaximumAssetBytes
                || expected.Sha256.Length != 64
                || !expected.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("The Monaco asset manifest contains invalid bounds or hashes.");
            total = checked(total + expected.Bytes);
            if (total > MaximumBundleBytes)
                throw new InvalidDataException("The Monaco asset bundle exceeds its safety limit.");
            var path = ResolveAssetPath(root, name);
            var info = new FileInfo(path);
            info.Refresh();
            if (!info.Exists || info.LinkTarget is not null || info.Length != expected.Bytes)
                throw new InvalidDataException($"The bundled Monaco asset '{name}' is missing or changed.");
            var content = File.ReadAllBytes(path);
            if (content.LongLength != expected.Bytes)
                throw new InvalidDataException($"The bundled Monaco asset '{name}' changed while loading.");
            var actualHash = Convert.ToHexString(SHA256.HashData(content));
            if (!string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The bundled Monaco asset '{name}' failed integrity validation.");
            verified.Add(name, content);
        }
        if (!manifest.Assets.ContainsKey("index.html") || !manifest.Assets.ContainsKey("editor.js"))
            throw new InvalidDataException("The Monaco asset manifest is incomplete.");
        return verified;
    }

    private static string ResolveAssetPath(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidDataException("A Monaco asset path escaped its bundle root.");
        return path;
    }

    private static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".ttf" => "font/ttf",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record AssetManifest
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyDictionary<string, AssetManifestEntry> Assets { get; init; } =
            new Dictionary<string, AssetManifestEntry>(StringComparer.Ordinal);
    }

    internal sealed record AssetManifestEntry
    {
        public long Bytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }
}
