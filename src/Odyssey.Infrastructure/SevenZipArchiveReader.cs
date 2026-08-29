using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using SharpSevenZip;

namespace Odyssey.Infrastructure;

internal sealed class SevenZipArchiveReader
{
    private const int MaximumEntries = 50_000;
    private const int MaximumCharacters = 2_000_000;
    private const long MaximumXmlEntryBytes = 32L * 1024 * 1024;
    private static readonly Lazy<SevenZipArchiveReader?> DefaultReader = new(CreateDefault,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private SevenZipArchiveReader(string libraryPath)
    {
        LibraryPath = libraryPath;
    }

    public static SevenZipArchiveReader? Default => DefaultReader.Value;
    public string LibraryPath { get; }

    public string Read(string extension, string archivePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var archive = new SharpSevenZipExtractor(archivePath);
            return extension is ".odt" or ".ods" or ".odp"
                ? ReadOpenDocument(archive, token)
                : ReadEntryNames(archive, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The archive could not be read safely by 7-Zip.", ex);
        }
    }

    private static SevenZipArchiveReader? CreateDefault()
    {
        foreach (var libraryPath in ResolveLibraryPaths())
        {
            try
            {
                if (!NativeLibrary.TryLoad(libraryPath, out var handle)) continue;
                try
                {
                    if (!NativeLibrary.TryGetExport(handle, "CreateObject", out _)) continue;
                }
                finally
                {
                    NativeLibrary.Free(handle);
                }
                SharpSevenZipBase.SetLibraryPath(libraryPath);
                return new SevenZipArchiveReader(libraryPath);
            }
            catch
            {
                // A bundled library can be incompatible with an older distribution.
                // Continue to an explicitly installed, interface-compatible candidate.
            }
        }
        return null;
    }

    private static string ReadEntryNames(SharpSevenZipExtractor archive, CancellationToken token)
    {
        var output = new StringBuilder();
        var count = 0;
        foreach (var entry in archive.ArchiveFileData)
        {
            token.ThrowIfCancellationRequested();
            if (count++ >= MaximumEntries || output.Length >= MaximumCharacters) break;
            AppendBounded(entry.FileName, output);
        }
        return output.ToString();
    }

    private static string ReadOpenDocument(SharpSevenZipExtractor archive, CancellationToken token)
    {
        foreach (var entry in archive.ArchiveFileData)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Encrypted || entry.Size > MaximumXmlEntryBytes ||
                !string.Equals(entry.FileName.Replace('\\', '/'), "content.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            using var content = new BoundedMemoryStream(MaximumXmlEntryBytes);
            archive.ExtractFile((int)entry.Index, content);
            token.ThrowIfCancellationRequested();
            content.Position = 0;
            return ReadXmlText(content, token);
        }
        return string.Empty;
    }

    private static string ReadXmlText(Stream stream, CancellationToken token)
    {
        var output = new StringBuilder();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });
        while (reader.Read() && output.Length < MaximumCharacters)
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                AppendBounded(reader.Value, output);
        }
        return output.ToString();
    }

    private static void AppendBounded(string? value, StringBuilder output)
    {
        if (string.IsNullOrWhiteSpace(value) || output.Length >= MaximumCharacters) return;
        if (output.Length > 0) output.Append(' ');
        var remaining = MaximumCharacters - output.Length;
        output.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
    }

    private static IEnumerable<string> ResolveLibraryPaths()
    {
        var configured = Environment.GetEnvironmentVariable("ODYSSEY_7ZIP_LIBRARY");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            yield return Path.GetFullPath(configured);

        var baseDirectory = AppContext.BaseDirectory;
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            var architecture = Environment.Is64BitProcess ? "x64" : "x86";
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates =
            [
                Path.Combine(baseDirectory, architecture, "7z.dll"),
                Path.Combine(baseDirectory, "7z.dll"),
                Path.Combine(programFiles, "7-Zip", "7z.dll")
            ];
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates =
            [
                Path.Combine(baseDirectory, "7z.so"),
                "/usr/libexec/7zip/7z.so",
                "/usr/lib64/7zip/7z.so",
                "/usr/lib/7zip/7z.so",
                "/usr/lib/p7zip/7z.so",
                "/usr/lib/x86_64-linux-gnu/7zip/7z.so",
                "/usr/lib/x86_64-linux-gnu/p7zip/7z.so",
                "/usr/lib/aarch64-linux-gnu/7zip/7z.so",
                "/usr/local/lib/7zip/7z.so"
            ];
        }
        else
        {
            candidates =
            [
                Path.Combine(baseDirectory, "7z.dylib"),
                "/usr/local/lib/7zip/7z.dylib",
                "/opt/homebrew/lib/7zip/7z.dylib"
            ];
        }
        foreach (var candidate in candidates.Where(File.Exists).Distinct(PathComparer))
            yield return candidate;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class BoundedMemoryStream(long maximumLength) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || Position > maximumLength - count)
                throw new InvalidDataException("The archive entry exceeds Odyssey's safe extraction limit.");
        }
    }
}
