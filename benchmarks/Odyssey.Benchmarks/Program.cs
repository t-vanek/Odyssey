using System.Text.Json;
using Odyssey.Benchmarks;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine(BenchmarkOptions.Usage);
    return 0;
}

try
{
    var options = BenchmarkOptions.Parse(args);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var report = await new OdysseyBenchmarkRunner(options).RunAsync(cancellation.Token);
    Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
    await File.WriteAllTextAsync(options.OutputPath, json);

    Console.WriteLine($"Odyssey benchmark: {(report.ValidationPassed ? "PASS" : "FAIL")}");
    Console.WriteLine($"Corpus: {report.Configuration.TotalFileCount:N0} files " +
                      $"({report.Configuration.PlainTextFileCount:N0} text + " +
                      $"{report.Configuration.RichDocumentCount:N0} rich documents)");
    Console.WriteLine($"Scan: {report.Scan.ItemsPerSecond:N0} entries/s");
    Console.WriteLine($"Content: {report.ContentIndex.FilesPerSecond:N0} files/s");
    Console.WriteLine("Content formats: " + string.Join(", ", report.ContentIndex.FilesByExtension
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => $"{pair.Key}={pair.Value:N0}")));
    Console.WriteLine($"Search median/p95: {report.SearchSummary.MedianLatencyMilliseconds:N2} / " +
                      $"{report.SearchSummary.P95LatencyMilliseconds:N2} ms");
    Console.WriteLine($"Minimum precision/recall: {report.SearchSummary.MinimumPrecision:P2} / " +
                      $"{report.SearchSummary.MinimumRecall:P2}");
    Console.WriteLine($"Verified copy: {report.Transfer.MiBPerSecond:N1} MiB/s");
    Console.WriteLine($"JSON: {options.OutputPath}");
    if (report.PreservedWorkspacePath is not null)
        Console.WriteLine($"Workspace: {report.PreservedWorkspacePath}");
    return report.ValidationPassed ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Benchmark cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(BenchmarkOptions.Usage);
    return 2;
}
