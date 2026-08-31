using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Odyssey.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using DrawingText = DocumentFormat.OpenXml.Drawing.Text;
using WordText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace Odyssey.Infrastructure;

public sealed class LocalContentExtractor : IContentExtractor, IOcrCapability
{
    private const int MaximumCharacters = 2_000_000;
    private const long MaximumSourceBytes = 64L * 1024 * 1024;
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".sql", ".cs", ".fs", ".vb", ".cpp", ".c", ".h",
        ".java", ".kt", ".py", ".js", ".ts", ".html", ".htm", ".css", ".sh", ".ps1"
    };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".odt", ".ods", ".odp", ".zip"
    };
    private static readonly HashSet<string> SevenZipArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso"
    };
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx"
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };
    private readonly string? _tesseractPath;
    private readonly string? _tessdataPath;
    private readonly SevenZipArchiveReader? _sevenZip = SevenZipArchiveReader.Default;
    private readonly HashSet<string> _supportedExtensions;

    public LocalContentExtractor(string? tesseractPath = null, string? tessdataPath = null)
    {
        _supportedExtensions = new HashSet<string>(
            PlainTextExtensions.Concat(ArchiveExtensions).Concat(OfficeExtensions).Append(".pdf"),
            StringComparer.OrdinalIgnoreCase);
        if (_sevenZip is not null) _supportedExtensions.UnionWith(SevenZipArchiveExtensions);
        var installation = ResolveInstallation(
            tesseractPath,
            tessdataPath,
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("PATH"));
        _tesseractPath = installation.ExecutablePath;
        _tessdataPath = installation.DataDirectory;
        var probe = ProbeTesseract(_tesseractPath, _tessdataPath);
        IsAvailable = probe.Available;
        Languages = probe.Languages;
        UnavailableReason = probe.Reason;
        if (IsAvailable) _supportedExtensions.UnionWith(ImageExtensions);
    }

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;
    public bool IsAvailable { get; }
    public IReadOnlyCollection<string> Languages { get; }
    public string? UnavailableReason { get; }

    public bool CanHandle(string extension) => SupportedExtensions.Contains(NormalizeExtension(extension));

    public async Task<ExtractedContent?> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = NormalizeExtension(Path.GetExtension(path));
        if (!CanHandle(extension)) return null;
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists) throw new FileNotFoundException("The file disappeared before content indexing.", path);
        if (before.Length > MaximumSourceBytes) return new ExtractedContent(string.Empty);
        var expectedLength = before.Length;
        var expectedModified = before.LastWriteTimeUtc;

        string text;
        if (ImageExtensions.Contains(extension))
            text = await ReadOcrAsync(path, cancellationToken).ConfigureAwait(false);
        else if (PlainTextExtensions.Contains(extension))
            text = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        else if (OfficeExtensions.Contains(extension))
            text = await Task.Run(() => ReadOffice(extension, path, cancellationToken), cancellationToken).ConfigureAwait(false);
        else if (extension == ".pdf")
            text = await Task.Run(() => ReadPdf(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        else
            text = await Task.Run(() => ReadArchive(extension, path, cancellationToken), cancellationToken).ConfigureAwait(false);

        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedModified)
            throw new IOException("The file changed during content indexing.");
        return new ExtractedContent(NormalizeWhitespace(text));
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 128 * 1024, leaveOpen: false);
        var output = new StringBuilder(Math.Min(MaximumCharacters, (int)Math.Min(stream.Length, int.MaxValue)));
        var buffer = new char[32 * 1024];
        while (output.Length < MaximumCharacters)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximumCharacters - output.Length)), token)
                .ConfigureAwait(false);
            if (read == 0) break;
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private static string ReadArchive(string extension, string path, CancellationToken token)
    {
        var sevenZip = SevenZipArchiveReader.Default;
        if (sevenZip is not null) return sevenZip.Read(extension, path, token);
        if (SevenZipArchiveExtensions.Contains(extension))
            throw new InvalidOperationException("The native 7-Zip engine is unavailable.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (extension == ".zip")
            return string.Join(' ', archive.Entries.Take(50_000).Select(entry => entry.FullName));

        var prefixes = new[] { "content.xml" };
        var output = new StringBuilder();
        foreach (var entry in archive.Entries.Where(entry =>
                     prefixes.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            token.ThrowIfCancellationRequested();
            if (output.Length >= MaximumCharacters) break;
            using var entryStream = entry.Open();
            AppendXmlText(entryStream, output, token);
        }
        return output.ToString();
    }

    private static string ReadOffice(string extension, string path, CancellationToken token)
    {
        var output = new StringBuilder();
        try
        {
            switch (extension)
            {
                case ".docx":
                    using (var document = WordprocessingDocument.Open(path, false))
                    {
                        AppendValues(document.MainDocumentPart?.Document?.Descendants<WordText>().Select(text => text.Text), output, token);
                        if (document.MainDocumentPart is not null)
                        {
                            foreach (var header in document.MainDocumentPart.HeaderParts)
                                AppendValues(header.Header?.Descendants<WordText>().Select(text => text.Text), output, token);
                            foreach (var footer in document.MainDocumentPart.FooterParts)
                                AppendValues(footer.Footer?.Descendants<WordText>().Select(text => text.Text), output, token);
                            AppendValues(document.MainDocumentPart.FootnotesPart?.Footnotes?
                                .Descendants<WordText>().Select(text => text.Text), output, token);
                            AppendValues(document.MainDocumentPart.EndnotesPart?.Endnotes?
                                .Descendants<WordText>().Select(text => text.Text), output, token);
                        }
                    }
                    break;
                case ".xlsx":
                    using (var document = SpreadsheetDocument.Open(path, false))
                    {
                        var workbook = document.WorkbookPart;
                        var shared = workbook?.SharedStringTablePart?.SharedStringTable?
                            .Elements<SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
                        if (workbook is not null)
                        {
                            foreach (var sheet in workbook.WorksheetParts)
                            foreach (var cell in sheet.Worksheet?.Descendants<Cell>() ?? [])
                            {
                                token.ThrowIfCancellationRequested();
                                var value = cell.DataType?.Value == CellValues.SharedString &&
                                            int.TryParse(cell.CellValue?.Text, out var index) && index >= 0 && index < shared.Length
                                    ? shared[index]
                                    : cell.InlineString?.InnerText ?? cell.CellValue?.Text;
                                AppendValue(value, output);
                                if (output.Length >= MaximumCharacters) return output.ToString();
                            }
                        }
                    }
                    break;
                case ".pptx":
                    using (var document = PresentationDocument.Open(path, false))
                    {
                        if (document.PresentationPart is not null)
                        foreach (var slide in document.PresentationPart.SlideParts)
                        {
                            AppendValues(slide.Slide?.Descendants<DrawingText>().Select(text => text.Text), output, token);
                            AppendValues(slide.NotesSlidePart?.NotesSlide?.Descendants<DrawingText>()
                                .Select(text => text.Text), output, token);
                            if (output.Length >= MaximumCharacters) break;
                        }
                    }
                    break;
            }
            return output.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The Office document could not be read safely.", ex);
        }
    }

    private static string ReadPdf(string path, CancellationToken token)
    {
        var output = new StringBuilder();
        try
        {
            using var document = PdfDocument.Open(path);
            foreach (var page in document.GetPages())
            {
                token.ThrowIfCancellationRequested();
                AppendValue(ContentOrderTextExtractor.GetText(page), output);
                if (output.Length >= MaximumCharacters) break;
            }
            return output.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException("The PDF document could not be read safely.", ex);
        }
    }

    private static void AppendValues(IEnumerable<string?>? values, StringBuilder output, CancellationToken token)
    {
        if (values is null) return;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            AppendValue(value, output);
            if (output.Length >= MaximumCharacters) break;
        }
    }

    private static void AppendValue(string? value, StringBuilder output)
    {
        if (string.IsNullOrWhiteSpace(value) || output.Length >= MaximumCharacters) return;
        if (output.Length > 0) output.Append(' ');
        var remaining = MaximumCharacters - output.Length;
        output.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
    }

    private async Task<string> ReadOcrAsync(string path, CancellationToken token)
    {
        if (!IsAvailable || _tesseractPath is null)
            throw new InvalidOperationException(UnavailableReason ?? "Czech and English OCR are unavailable.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _tesseractPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("stdout");
        AddTessdataArgument(process.StartInfo, _tessdataPath);
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add("ces+eng");
        process.StartInfo.ArgumentList.Add("--oem");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--psm");
        process.StartInfo.ArgumentList.Add("3");
        process.Start();
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "OCR failed." : error.Trim());
            return output.Length <= MaximumCharacters ? output : output[..MaximumCharacters];
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw;
        }
    }

    private static (bool Available, IReadOnlyCollection<string> Languages, string? Reason) ProbeTesseract(
        string? executable,
        string? tessdataPath)
    {
        if (executable is null) return (false, Array.Empty<string>(), "Tesseract was not found.");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            AddTessdataArgument(process.StartInfo, tessdataPath);
            process.StartInfo.ArgumentList.Add("--list-langs");
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return (false, Array.Empty<string>(), "Tesseract language detection timed out.");
            }
            var languages = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value is "ces" or "eng").Distinct(StringComparer.Ordinal).Order().ToArray();
            var available = languages.Contains("ces") && languages.Contains("eng");
            return available
                ? (true, languages, null)
                : (false, languages, "Both Czech (ces) and English (eng) Tesseract data are required.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, Array.Empty<string>(), ex.Message);
        }
    }

    internal static TesseractInstallation ResolveInstallation(
        string? configuredPath,
        string? configuredDataDirectory,
        string applicationDirectory,
        string? searchPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return new TesseractInstallation(
                File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null,
                NormalizeDataDirectory(configuredDataDirectory));

        var fileName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        var packagedDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "ocr");
        var packagedExecutable = Path.Combine(packagedDirectory, fileName);
        if (File.Exists(packagedExecutable))
            return new TesseractInstallation(packagedExecutable, Path.Combine(packagedDirectory, "tessdata"));

        foreach (var directory in (searchPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return new TesseractInstallation(candidate, NormalizeDataDirectory(configuredDataDirectory));
            }
            catch { }
        }
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe");
            if (File.Exists(candidate))
                return new TesseractInstallation(candidate, NormalizeDataDirectory(configuredDataDirectory));
        }
        return new TesseractInstallation(null, NormalizeDataDirectory(configuredDataDirectory));
    }

    private static string? NormalizeDataDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static void AddTessdataArgument(ProcessStartInfo startInfo, string? tessdataPath)
    {
        if (tessdataPath is null) return;
        startInfo.ArgumentList.Add("--tessdata-dir");
        startInfo.ArgumentList.Add(tessdataPath);
    }

    private static void AppendXmlText(Stream stream, StringBuilder output, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaximumCharacters * 2L
        });
        while (output.Length < MaximumCharacters && reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA)) continue;
            if (output.Length > 0) output.Append(' ');
            var remaining = MaximumCharacters - output.Length;
            output.Append(reader.Value.AsSpan(0, Math.Min(remaining, reader.Value.Length)));
        }
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(Math.Min(value.Length, MaximumCharacters));
        var previousWhitespace = false;
        foreach (var character in value)
        {
            var whitespace = char.IsWhiteSpace(character);
            if (!whitespace) result.Append(character);
            else if (!previousWhitespace) result.Append(' ');
            previousWhitespace = whitespace;
            if (result.Length >= MaximumCharacters) break;
        }
        return result.ToString().Trim();
    }

    private static string NormalizeExtension(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        (value.StartsWith('.') ? value : $".{value}").ToLowerInvariant();
}

internal sealed record TesseractInstallation(string? ExecutablePath, string? DataDirectory);
