using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Odyssey.Benchmarks;

internal sealed class OdysseyBenchmarkRunner(BenchmarkOptions options)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async Task<BenchmarkReport> RunAsync(CancellationToken cancellationToken)
    {
        var workspaceBase = options.WorkspaceBase ?? Path.GetTempPath();
        Directory.CreateDirectory(workspaceBase);
        var runRoot = Path.Combine(workspaceBase,
            $"odyssey-benchmark-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        try
        {
            var corpusRoot = Path.Combine(runRoot, "corpus");
            var corpusClock = Stopwatch.StartNew();
            var corpus = await GenerateCorpusAsync(
                corpusRoot, options.FileCount, options.RichDocumentCount, cancellationToken);
            corpusClock.Stop();
            var manifestPath = Path.Combine(runRoot, "benchmark-corpus-manifest.json");
            var totalFileCount = options.FileCount + options.RichDocumentCount;
            await WriteCorpusManifestAsync(
                manifestPath, corpusRoot, options.FileCount, options.RichDocumentCount,
                corpus.FilesByExtension, corpus.Queries, cancellationToken);
            var corpusMetric = new CorpusMetric(
                corpusClock.Elapsed.TotalMilliseconds,
                corpus.BytesWritten,
                Rate(totalFileCount, corpusClock.Elapsed),
                corpus.FilesByExtension);

            var performance = SystemPerformanceProfile.Current;
            var storage = new ApplicationStorage(Path.Combine(runRoot, "application-data"));
            var connections = new SqliteConnectionFactory(storage, performance, pooling: false);
            var store = new SqliteOdysseyStore(connections, NullLogger<SqliteOdysseyStore>.Instance);
            var search = new SqliteSearchService(connections, NullLogger<SqliteSearchService>.Instance);
            await store.InitializeAsync(cancellationToken);
            await search.InitializeAsync(cancellationToken);
            var scanner = new ScanCoordinator(
                new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
                new ExtensionFileClassifier(),
                store,
                new ScanPipelineOptions(PerformanceProfile: performance),
                NullLogger<ScanCoordinator>.Instance);
            var session = await store.SaveSessionAsync(new RescueSession
            {
                Id = Guid.NewGuid(),
                Name = "Odyssey deterministic benchmark",
                Mode = SessionMode.ForensicReadOnly,
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            var target = await store.SaveTargetAsync(new ScanTarget
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                RootPath = corpusRoot
            }, cancellationToken);

            var scanClock = Stopwatch.StartNew();
            var scanResult = await scanner.ScanAsync(target, null, cancellationToken);
            scanClock.Stop();
            var analysis = await store.GetAnalysisAsync(session.Id, cancellationToken);
            var scanMetric = SnapshotPhase(scanClock.Elapsed, analysis.TotalFiles + analysis.TotalDirectories);

            var contentClock = Stopwatch.StartNew();
            var contentResult = await IndexContentsAsync(
                store, session.Id, performance, cancellationToken);
            contentClock.Stop();
            var contentMetric = new ContentMetric(
                contentClock.Elapsed.TotalMilliseconds,
                contentResult.Files,
                contentResult.Errors,
                Rate(contentResult.Files, contentClock.Elapsed),
                Process.GetCurrentProcess().PeakWorkingSet64,
                GC.GetTotalMemory(forceFullCollection: false),
                contentResult.FilesByExtension,
                contentResult.ErrorsByExtension);

            await search.WarmupAsync(session.Id, cancellationToken);
            var searchMetrics = new List<SearchMetric>(corpus.Queries.Count);
            foreach (var query in corpus.Queries)
                searchMetrics.Add(await MeasureSearchAsync(
                    search, session.Id, query, options.SearchIterations, cancellationToken));
            var allLatencies = searchMetrics
                .SelectMany(metric => metric.LatencySamplesMilliseconds)
                .ToArray();
            var searchSummary = new SearchSummary(
                searchMetrics.Min(metric => metric.Precision),
                searchMetrics.Min(metric => metric.Recall),
                Percentile(allLatencies, 0.5),
                Percentile(allLatencies, 0.95));

            var transfer = await MeasureTransferAsync(storage, runRoot, options.TransferMiB, cancellationToken);
            var valid = scanResult.Status == ScanStatus.Completed
                        && analysis.TotalFiles == totalFileCount
                        && contentResult.Files == totalFileCount
                        && contentResult.Errors == 0
                        && searchMetrics.All(metric => metric.Precision == 1d && metric.Recall == 1d)
                        && transfer.Sha256Verified;

            return new BenchmarkReport(
                2,
                DateTimeOffset.UtcNow,
                new BenchmarkEnvironment(
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    performance.ProcessorCount,
                    performance.AvailableMemoryBytes,
                    Environment.GetEnvironmentVariable("ODYSSEY_BENCHMARK_COMMIT")
                    ?? Environment.GetEnvironmentVariable("GITHUB_SHA")),
                new BenchmarkConfiguration(
                    options.FileCount,
                    options.RichDocumentCount,
                    totalFileCount,
                    options.SearchIterations,
                    options.TransferMiB),
                corpusMetric,
                scanMetric,
                contentMetric,
                searchMetrics,
                searchSummary,
                transfer,
                valid,
                options.KeepWorkspace ? runRoot : null,
                options.KeepWorkspace ? manifestPath : null);
        }
        finally
        {
            if (!options.KeepWorkspace && Directory.Exists(runRoot))
                Directory.Delete(runRoot, recursive: true);
        }
    }

    private static async Task<GeneratedCorpus> GenerateCorpusAsync(
        string root,
        int fileCount,
        int richDocumentCount,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var expected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["girlanda-final"] = new HashSet<string>(PathComparer),
            ["invoice-ostrava"] = new HashSet<string>(PathComparer),
            ["nebula-contract"] = new HashSet<string>(PathComparer),
            ["approval-lighthouse"] = new HashSet<string>(PathComparer),
            ["unique"] = new HashSet<string>(PathComparer),
            ["negative"] = new HashSet<string>(PathComparer),
            ["rich-pdf"] = new HashSet<string>(PathComparer),
            ["rich-docx"] = new HashSet<string>(PathComparer),
            ["rich-xlsx"] = new HashSet<string>(PathComparer),
            ["rich-pptx"] = new HashSet<string>(PathComparer),
            ["rich-zip"] = new HashSet<string>(PathComparer)
        };
        var entries = new List<(string Path, string Content)>(fileCount);
        var uniqueIndex = fileCount / 2;
        for (var index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.Combine(root, $"group-{index % 128:000}");
            var nameTokens = new List<string>();
            if (index % 97 == 0) nameTokens.Add("girlanda-final");
            if (index % 131 == 0) nameTokens.Add("invoice-ostrava");
            if (index == uniqueIndex) nameTokens.Add($"odyssey-needle-{index:00000000}");
            if (index % 97 != 0 && index % 12 == 1) nameTokens.Add("girlanda-draft");
            if (index % 97 != 0 && index % 12 == 2) nameTokens.Add("final-summary");
            if (index % 131 != 0 && index % 12 == 3) nameTokens.Add("invoice-archive");
            if (index % 131 != 0 && index % 12 == 4) nameTokens.Add("ostrava-notes");
            var prefix = nameTokens.Count == 0 ? string.Empty : string.Join('-', nameTokens) + '-';
            var path = Path.Combine(folder, $"{prefix}document-{index:00000000}.txt");
            var contentTokens = new List<string>();
            if (index % 89 == 0) contentTokens.Add("nebula contract");
            if (index % 149 == 0) contentTokens.Add("approval lighthouse");
            if (index % 89 != 0 && index % 12 == 5) contentTokens.Add("nebula observation");
            if (index % 89 != 0 && index % 12 == 6) contentTokens.Add("contract archive");
            if (index % 149 != 0 && index % 12 == 7) contentTokens.Add("approval draft");
            if (index % 149 != 0 && index % 12 == 8) contentTokens.Add("lighthouse photograph");
            var content = $"Deterministic Odyssey benchmark document {index:00000000}. " +
                          (contentTokens.Count == 0 ? "ordinary reference material" : string.Join(' ', contentTokens));
            entries.Add((path, content));
            if (index % 97 == 0) expected["girlanda-final"].Add(path);
            if (index % 131 == 0) expected["invoice-ostrava"].Add(path);
            if (index % 89 == 0) expected["nebula-contract"].Add(path);
            if (index % 149 == 0) expected["approval-lighthouse"].Add(path);
            if (index == uniqueIndex) expected["unique"].Add(path);
        }
        foreach (var directory in entries.Select(entry => Path.GetDirectoryName(entry.Path)!).Distinct(PathComparer))
            Directory.CreateDirectory(directory);
        await Parallel.ForEachAsync(entries, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 16)
        }, async (entry, token) => await File.WriteAllTextAsync(entry.Path, entry.Content, token));

        var richRoot = Path.Combine(root, "rich-documents");
        Directory.CreateDirectory(richRoot);
        for (var index = 0; index < richDocumentCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = index % 5;
            var extension = format switch
            {
                0 => ".pdf",
                1 => ".docx",
                2 => ".xlsx",
                3 => ".pptx",
                _ => ".zip"
            };
            var path = Path.Combine(richRoot, $"rich-document-{index:0000}{extension}");
            switch (format)
            {
                case 0:
                    await CreatePdfAsync(path,
                        $"Odyssey rich PDF {index:0000}: papyrus comet recovery evidence.", cancellationToken);
                    expected["rich-pdf"].Add(path);
                    break;
                case 1:
                    CreateDocx(path, $"Odyssey rich Word {index:0000}: word atlas recovery evidence.");
                    expected["rich-docx"].Add(path);
                    break;
                case 2:
                    CreateXlsx(path, $"Odyssey rich spreadsheet {index:0000}: ledger sapphire recovery evidence.");
                    expected["rich-xlsx"].Add(path);
                    break;
                case 3:
                    CreatePptx(path, $"Odyssey rich presentation {index:0000}: slides harbor recovery evidence.");
                    expected["rich-pptx"].Add(path);
                    break;
                default:
                    CreateZip(path, $"archives/zip-cipher-{index:0000}.txt");
                    expected["rich-zip"].Add(path);
                    break;
            }
        }

        var generatedFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        var bytes = generatedFiles.Sum(path => new FileInfo(path).Length);
        var filesByExtension = generatedFiles
            .GroupBy(path => NormalizeExtension(Path.GetExtension(path)), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);

        return new GeneratedCorpus(bytes,
        [
            new BenchmarkQuery("filename-common", "girlanda final", expected["girlanda-final"]),
            new BenchmarkQuery("filename-secondary", "invoice ostrava", expected["invoice-ostrava"]),
            new BenchmarkQuery("content-common", "nebula contract", expected["nebula-contract"]),
            new BenchmarkQuery("content-secondary", "approval lighthouse", expected["approval-lighthouse"]),
            new BenchmarkQuery("filename-unique", $"odyssey needle {uniqueIndex:00000000}", expected["unique"]),
            new BenchmarkQuery("rich-pdf-content", "papyrus comet", expected["rich-pdf"]),
            new BenchmarkQuery("rich-docx-content", "word atlas", expected["rich-docx"]),
            new BenchmarkQuery("rich-xlsx-content", "ledger sapphire", expected["rich-xlsx"]),
            new BenchmarkQuery("rich-pptx-content", "slides harbor", expected["rich-pptx"]),
            new BenchmarkQuery("rich-zip-entry", "zip cipher", expected["rich-zip"]),
            new BenchmarkQuery("negative", "quasar nonexistent", expected["negative"])
        ], filesByExtension);
    }

    private static async Task CreatePdfAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(40, 760), font);
        await File.WriteAllBytesAsync(path, builder.Build(), cancellationToken);
    }

    private static void CreateDocx(string path, string text)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
        main.Document.Save();
    }

    private static void CreateXlsx(string path, string text)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();
        var sharedStrings = workbook.AddNewPart<SharedStringTablePart>();
        sharedStrings.SharedStringTable = new S.SharedStringTable(
            new S.SharedStringItem(new S.Text(text)));
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
        {
            DataType = S.CellValues.SharedString,
            CellValue = new S.CellValue("0")
        })));
        workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet
        {
            Id = workbook.GetIdOfPart(worksheet),
            SheetId = 1,
            Name = "Recovery evidence"
        }));
        workbook.Workbook.Save();
    }

    private static void CreatePptx(string path, string text)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        presentation.Presentation = new P.Presentation();
        var slide = presentation.AddNewPart<SlidePart>();
        slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2, Name = "Recovery evidence" },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(text))))))));
        var slideIds = presentation.Presentation.AppendChild(new P.SlideIdList());
        slideIds.Append(new P.SlideId
        {
            Id = 256,
            RelationshipId = presentation.GetIdOfPart(slide)
        });
        presentation.Presentation.Save();
    }

    private static void CreateZip(string path, string entryName)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("Deterministic archive member for Odyssey benchmark.");
    }

    private static async Task<ContentIndexResult> IndexContentsAsync(
        IOdysseyStore store,
        Guid sessionId,
        SystemPerformanceProfile performance,
        CancellationToken cancellationToken)
    {
        var extractor = new LocalContentExtractor();
        long files = 0;
        long errors = 0;
        var filesByExtension = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var errorsByExtension = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        while (true)
        {
            var candidates = await store.GetContentExtractionCandidatesAsync(
                sessionId, extractor.SupportedExtensions, 1_000, cancellationToken);
            if (candidates.Count == 0) break;
            var updates = new ConcurrentBag<ContentIndexUpdate>();
            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(performance.ProcessorCount / 2, 1, 4)
            }, async (candidate, token) =>
            {
                var extension = NormalizeExtension(candidate.Extension);
                try
                {
                    var extracted = await extractor.ExtractAsync(candidate.FullPath, token);
                    updates.Add(new ContentIndexUpdate(
                        candidate.FileId, candidate.ModifiedAt, extracted?.Text ?? string.Empty, null));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref errors);
                    errorsByExtension.AddOrUpdate(extension, 1, static (_, count) => count + 1);
                    updates.Add(new ContentIndexUpdate(
                        candidate.FileId, candidate.ModifiedAt, null, exception.Message));
                }
                Interlocked.Increment(ref files);
                filesByExtension.AddOrUpdate(extension, 1, static (_, count) => count + 1);
            });
            await store.UpdateExtractedContentsAsync(updates.ToArray(), cancellationToken);
        }
        return new ContentIndexResult(
            files,
            errors,
            filesByExtension.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            errorsByExtension.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private static async Task<SearchMetric> MeasureSearchAsync(
        ISearchService search,
        Guid sessionId,
        BenchmarkQuery benchmarkQuery,
        int iterations,
        CancellationToken cancellationToken)
    {
        var latencies = new double[iterations];
        IReadOnlyList<SearchResult> results = [];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var clock = Stopwatch.StartNew();
            results = await ReadAllResultsAsync(
                search, sessionId, benchmarkQuery.Query, cancellationToken);
            clock.Stop();
            latencies[iteration] = clock.Elapsed.TotalMilliseconds;
        }

        var returned = results.Select(result => Path.GetFullPath(result.FullPath)).ToHashSet(PathComparer);
        var truePositives = returned.Count(benchmarkQuery.ExpectedPaths.Contains);
        var falsePositives = returned.Count - truePositives;
        var falseNegatives = benchmarkQuery.ExpectedPaths.Count - truePositives;
        return new SearchMetric(
            benchmarkQuery.Name,
            benchmarkQuery.Query,
            benchmarkQuery.ExpectedPaths.Count,
            returned.Count,
            truePositives,
            falsePositives,
            falseNegatives,
            returned.Count == 0
                ? benchmarkQuery.ExpectedPaths.Count == 0 ? 1 : 0
                : (double)truePositives / returned.Count,
            benchmarkQuery.ExpectedPaths.Count == 0 ? 1 : (double)truePositives / benchmarkQuery.ExpectedPaths.Count,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            latencies);
    }

    private static async Task<IReadOnlyList<SearchResult>> ReadAllResultsAsync(
        ISearchService search,
        Guid sessionId,
        string query,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1_000;
        var results = new List<SearchResult>();
        for (var offset = 0; ; offset += pageSize)
        {
            var response = await search.SearchAsync(new SearchRequest
            {
                Query = query,
                SessionId = sessionId,
                Limit = pageSize,
                Offset = offset
            }, cancellationToken);
            results.AddRange(response.Results);
            if (!response.HasMore) return results;
        }
    }

    private static async Task<TransferMetric> MeasureTransferAsync(
        ApplicationStorage storage,
        string runRoot,
        int transferMiB,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(runRoot, "transfer-source");
        var destinationDirectory = Path.Combine(runRoot, "transfer-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "benchmark-transfer.bin");
        var bytes = (long)transferMiB * 1024 * 1024;
        var buffer = new byte[1024 * 1024];
        new Random(20260830).NextBytes(buffer);
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (long written = 0; written < bytes; written += buffer.Length)
            {
                var count = (int)Math.Min(buffer.Length, bytes - written);
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        var clock = Stopwatch.StartNew();
        var outcome = await operations.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            VerifyAfterCopy = true
        }, cancellationToken: cancellationToken);
        clock.Stop();
        return new TransferMetric(
            bytes,
            clock.Elapsed.TotalMilliseconds,
            bytes / 1024d / 1024d / Math.Max(clock.Elapsed.TotalSeconds, 0.000001),
            outcome.Verified);
    }

    private static async Task WriteCorpusManifestAsync(
        string path,
        string corpusRoot,
        int plainTextFileCount,
        int richDocumentCount,
        IReadOnlyDictionary<string, long> filesByExtension,
        IReadOnlyList<BenchmarkQuery> queries,
        CancellationToken cancellationToken)
    {
        var manifest = new BenchmarkCorpusManifest(
            2,
            plainTextFileCount,
            richDocumentCount,
            plainTextFileCount + richDocumentCount,
            filesByExtension,
            queries.Select(query => new BenchmarkManifestQuery(
                query.Name,
                query.Query,
                query.ExpectedPaths
                    .Select(expected => Path.GetRelativePath(corpusRoot, expected))
                    .Order(PathComparer)
                    .ToArray())).ToArray());
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string NormalizeExtension(string? extension) =>
        string.IsNullOrWhiteSpace(extension) ? "(none)" : extension.ToLowerInvariant();

    private static PhaseMetric SnapshotPhase(TimeSpan elapsed, long items) => new(
        elapsed.TotalMilliseconds,
        items,
        Rate(items, elapsed),
        Process.GetCurrentProcess().PeakWorkingSet64,
        GC.GetTotalMemory(forceFullCollection: false));

    private static double Rate(long items, TimeSpan elapsed) =>
        items / Math.Max(elapsed.TotalSeconds, 0.000001);

    private static double Percentile(IEnumerable<double> source, double percentile)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}
