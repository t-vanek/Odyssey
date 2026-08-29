using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task MultipleKeywordsPrefixesAndMetadataFilters_ReturnSensibleResults()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "Ostrava", "archive");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var entries = new[]
        {
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Projekt_Ostravice_Kanalizace.pdf"), 5_000),
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Rozpocet_Ostravice.xlsx"), 3_000),
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Contract_Novak_2021.docx"), 7_000),
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Family_Photos_2019"), 1_000),
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "random.txt"), 10)
        };
        await environment.Store.UpsertEntriesAsync(created.Scan.Id, entries);

        var ostrava = await environment.Search.SearchAsync(new SearchRequest { Query = "ostrava", SessionId = created.Session.Id }, CancellationToken.None);
        var multi = await environment.Search.SearchAsync(new SearchRequest { Query = "ostrava kanal", SessionId = created.Session.Id }, CancellationToken.None);
        var novak = await environment.Search.SearchAsync(new SearchRequest { Query = "novak 2021", Extension = "docx", MinimumSize = 6_000, SessionId = created.Session.Id }, CancellationToken.None);
        var family = await environment.Search.SearchAsync(new SearchRequest { Query = "family photos", SessionId = created.Session.Id }, CancellationToken.None);

        Assert.True(ostrava.Results.Count >= 2);
        Assert.Equal("Projekt_Ostravice_Kanalizace.pdf", Assert.Single(multi.Results).Name);
        Assert.Equal("Contract_Novak_2021.docx", Assert.Single(novak.Results).Name);
        Assert.Equal("Family_Photos_2019", Assert.Single(family.Results).Name);
    }

    [Fact]
    public async Task FilenameMatch_RanksAheadOfPathOnlyMatch()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "target");
        var created = await environment.CreateInvestigationAsync(targetPath);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Novak", "unrelated.txt")),
            TestEntries.File(created.Target.Id, Path.Combine(targetPath, "Contract_Novak_2021.docx"))
        ]);

        var response = await environment.Search.SearchAsync(new SearchRequest { Query = "novak", SessionId = created.Session.Id }, CancellationToken.None);
        Assert.Equal("Contract_Novak_2021.docx", response.Results.First().Name);
        var nameMatch = response.Results.Single(result => result.Name == "Contract_Novak_2021.docx");
        var pathMatch = response.Results.Single(result => result.Name == "unrelated.txt");
        Assert.InRange(nameMatch.Score, 0d, 1d);
        Assert.True(nameMatch.Score > pathMatch.Score);
        Assert.True(nameMatch.MatchEvidence.HasFlag(SearchMatchEvidence.Name));
        Assert.True(pathMatch.MatchEvidence.HasFlag(SearchMatchEvidence.Path));
    }

    [Fact]
    public async Task Suggestions_UseRememberedQueriesAndNeverInventFromIndexedNames()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "suggestions"));
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, Path.Combine(created.Target.RootPath, "Projekt_Ostravice_Kanalizace.pdf")),
            TestEntries.File(created.Target.Id, Path.Combine(created.Target.RootPath, "Rozpocet_Ostravice.xlsx"))
        ]);

        var withoutHistory = await environment.Search.SuggestAsync(new SearchSuggestionRequest
        {
            Query = "ostrav",
            SessionId = created.Session.Id
        }, CancellationToken.None);
        Assert.Empty(withoutHistory);

        await environment.Search.RememberSearchAsync("Ostrava kanalizace 2021", created.Session.Id);
        await environment.Search.RememberSearchAsync("Ostrava kanalizace 2021", created.Session.Id);
        var remembered = await environment.Search.SuggestAsync(new SearchSuggestionRequest
        {
            Query = "ostr",
            SessionId = created.Session.Id
        }, CancellationToken.None);

        Assert.Equal(SearchSuggestionKind.History, remembered.First().Kind);
        Assert.Equal("Ostrava kanalizace 2021", remembered.First().Text);
        Assert.True(remembered.Count <= 7);
    }

    [Fact]
    public async Task SystemHistory_ReadsOnlyRealSearchUrisAndFiltersByPrefix()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var xbel = Path.Combine(environment.Root, "recently-used.xbel");
        await File.WriteAllTextAsync(xbel, """
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks">
              <bookmark href="baloosearch:/?query=Projekt%20Ostravice" />
              <bookmark href="filenamesearch:?search=Rozpocet+2024&amp;url=file%3A%2F%2F%2Ftmp" />
              <bookmark href="file:///tmp/not-a-search.txt" />
            </xbel>
            """);

        var extracted = SystemSearchHistoryService.ReadSearchUrisFromXbel(xbel);
        Assert.Equal(["Projekt Ostravice", "Rozpocet 2024"], extracted);

        var service = new SystemSearchHistoryService(() => extracted);
        await service.WarmupAsync();
        Assert.Equal(["Projekt Ostravice"], service.Suggest("proj", 7));
        Assert.Empty(service.Suggest("not", 7));
    }

    [Fact]
    public async Task Warmup_CachesHistoryAndRememberingAQueryRefreshesIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "warm-history"));
        await environment.Search.RememberSearchAsync("Projekt Ostravice 2021", created.Session.Id);

        await environment.Search.WarmupAsync(created.Session.Id);
        var warmed = await environment.Search.SuggestAsync(new SearchSuggestionRequest
        {
            Query = "proj",
            SessionId = created.Session.Id
        }, CancellationToken.None);
        Assert.Equal("Projekt Ostravice 2021", warmed.First().Text);
        Assert.Equal(SearchSuggestionKind.History, warmed.First().Kind);

        await environment.Search.RememberSearchAsync("Rozpočet rekonstrukce", created.Session.Id);
        var refreshed = await environment.Search.SuggestAsync(new SearchSuggestionRequest
        {
            Query = "rozp",
            SessionId = created.Session.Id
        }, CancellationToken.None);
        Assert.Equal("Rozpočet rekonstrukce", refreshed.First().Text);
        Assert.Equal(SearchSuggestionKind.History, refreshed.First().Kind);
    }

    [Fact]
    public async Task CategoryTargetSizeAndDateFilters_AreAppliedInSql()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var first = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "one"));
        var secondTarget = await environment.Store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = first.Session.Id,
            RootPath = Path.Combine(environment.Root, "two")
        });
        await environment.Store.UpsertEntriesAsync(first.Scan.Id, [TestEntries.File(first.Target.Id, Path.Combine(first.Target.RootPath, "photo.jpg"), 2_000)]);
        var secondScan = new ScanSession { Id = Guid.NewGuid(), TargetId = secondTarget.Id, StartedAt = DateTimeOffset.UtcNow, Status = ScanStatus.Running };
        await environment.Store.StartScanAsync(secondScan);
        await environment.Store.UpsertEntriesAsync(secondScan.Id, [TestEntries.File(secondTarget.Id, Path.Combine(secondTarget.RootPath, "photo-old.jpg"), 50)]);

        var response = await environment.Search.SearchAsync(new SearchRequest
        {
            Query = "photo",
            SessionId = first.Session.Id,
            TargetId = first.Target.Id,
            Category = FileCategory.Images,
            MinimumSize = 1_000,
            ModifiedFrom = DateTimeOffset.UtcNow.AddDays(-2)
        }, CancellationToken.None);
        Assert.Equal("photo.jpg", Assert.Single(response.Results).Name);
    }

    [Fact]
    public async Task SearchPagesAreCompleteNonOverlappingAndReportContinuation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "pages"));
        var entries = Enumerable.Range(0, 7)
            .Select(index => TestEntries.File(created.Target.Id,
                Path.Combine(created.Target.RootPath, $"result-{index:D2}.txt"), 10,
                DateTimeOffset.UtcNow.AddMinutes(-index)))
            .ToArray();
        await environment.Store.UpsertEntriesAsync(created.Scan.Id, entries);

        var first = await environment.Search.SearchAsync(new SearchRequest
            { Query = "result", SessionId = created.Session.Id, Limit = 3 }, CancellationToken.None);
        var second = await environment.Search.SearchAsync(new SearchRequest
            { Query = "result", SessionId = created.Session.Id, Limit = 3, Offset = 3 }, CancellationToken.None);
        var last = await environment.Search.SearchAsync(new SearchRequest
            { Query = "result", SessionId = created.Session.Id, Limit = 3, Offset = 6 }, CancellationToken.None);

        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(last.HasMore);
        Assert.Equal(7, first.Results.Concat(second.Results).Concat(last.Results).Select(x => x.FileId).Distinct().Count());
    }
}
