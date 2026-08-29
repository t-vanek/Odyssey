using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class BackgroundAutomationTests
{
    [Fact]
    public async Task Startup_ResumesInterruptedScan_AndMakesFilesSearchable()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "resume-target"));
        var path = Path.Combine(created.Target.RootPath, "restored-work.txt");
        await File.WriteAllTextAsync(path, "valuable restored work");
        await environment.Store.SaveScanCheckpointAsync(created.Scan.Id,
            new ScanProgress(1, 0, 0, 0, 0, TimeSpan.FromMilliseconds(50), path));
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 32, 8), NullLogger<ScanCoordinator>.Instance);
        var options = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(40), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1));
        await using var automation = new BackgroundAutomationService(
            environment.Store, coordinator, new LocalContentExtractor(),
            SystemPerformanceProfile.ForHardware(4, 4L * 1024 * 1024 * 1024),
            NullLogger<BackgroundAutomationService>.Instance, options);
        var activities = new List<BackgroundActivity>();
        automation.StatusChanged += (_, status) =>
        {
            lock (activities) activities.Add(status.Activity);
        };

        await automation.StartAsync(created.Session.Id, [created.Target]);
        SearchResponse? response = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            response = await environment.Search.SearchAsync(new SearchRequest
            { Query = "restored", SessionId = created.Session.Id }, CancellationToken.None);
            if (response.Results.Count > 0) break;
            await Task.Delay(30);
        }

        Assert.Equal("restored-work.txt", Assert.Single(response!.Results).Name);
        lock (activities) Assert.Contains(BackgroundActivity.ResumingScan, activities);
    }

    [Fact]
    public async Task ApprovedTargetChange_IsDebouncedScannedAndMadeSearchable()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "watched"));
        await environment.Store.CompleteScanAsync(created.Scan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 32, 8), NullLogger<ScanCoordinator>.Instance);
        var options = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(40), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1));
        await using var automation = new BackgroundAutomationService(
            environment.Store, coordinator, new LocalContentExtractor(),
            SystemPerformanceProfile.ForHardware(4, 4L * 1024 * 1024 * 1024),
            NullLogger<BackgroundAutomationService>.Instance, options);
        var activities = new List<BackgroundActivity>();
        automation.StatusChanged += (_, status) => { lock (activities) activities.Add(status.Activity); };
        await automation.StartAsync(created.Session.Id, [created.Target]);

        var path = Path.Combine(created.Target.RootPath, "automatic-result.txt");
        await File.WriteAllTextAsync(path, "background content");
        SearchResponse? response = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            response = await environment.Search.SearchAsync(new SearchRequest
            { Query = "automatic", SessionId = created.Session.Id }, CancellationToken.None);
            if (response.Results.Count > 0) break;
            await Task.Delay(30);
        }

        Assert.Equal("automatic-result.txt", Assert.Single(response!.Results).Name);
        lock (activities) Assert.Contains(BackgroundActivity.ScanningChanges, activities);
        Assert.Equal("background content", await File.ReadAllTextAsync(path));
    }
}
