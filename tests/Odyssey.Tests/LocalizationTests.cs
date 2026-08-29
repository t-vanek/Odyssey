using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void CzechAndEnglishExposeTheSameResourceKeys()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var english = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            typeof(LocalizationService).GetField("English", flags)!.GetValue(null));
        var czech = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            typeof(LocalizationService).GetField("Czech", flags)!.GetValue(null));

        Assert.Empty(english.Keys.Except(czech.Keys));
        Assert.Empty(czech.Keys.Except(english.Keys));
    }

    [Fact]
    public void LanguageCanSwitchImmediately_AndPreferencePersists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-language-{Guid.NewGuid():N}");
        try
        {
            var storage = new ApplicationStorage(root);
            var localization = new LocalizationService(storage);

            localization.SelectedLanguage = localization.Languages.Single(language => language.Code == "cs");
            Assert.Equal("Dokumenty", localization.TranslateEnum(FileCategory.Documents));
            Assert.Equal("Prohledat", localization["Scan"]);
            Assert.Equal("Co hledáte?", localization["RescueTitle"]);
            Assert.Equal("Jednoduchý režim", localization["MenuRescue"]);
            Assert.Contains("vše, co si pamatujete", localization["RescueSubtitle"]);
            Assert.Equal("Připravuji Odyssey…", localization["SplashPreparing"]);
            Assert.NotEqual("RescueNoResultsDetail", localization["RescueNoResultsDetail"]);
            Assert.NotEqual("RescueSearchFailedDetail", localization["RescueSearchFailedDetail"]);

            var reloaded = new LocalizationService(storage);
            Assert.Equal("cs", reloaded.SelectedLanguage.Code);

            reloaded.SelectedLanguage = reloaded.Languages.Single(language => language.Code == "en");
            Assert.Equal("Documents", reloaded.TranslateEnum(FileCategory.Documents));
            Assert.Equal("Scan", reloaded["Scan"]);
            Assert.Equal("What are you looking for?", reloaded["RescueTitle"]);
            Assert.Equal("Simple mode", reloaded["MenuRescue"]);
            Assert.Contains("anything you remember", reloaded["RescueSubtitle"]);
            Assert.Equal("Preparing Odyssey…", reloaded["SplashPreparing"]);
            Assert.NotEqual("RescueNoResultsDetail", reloaded["RescueNoResultsDetail"]);
            Assert.NotEqual("RescueSearchFailedDetail", reloaded["RescueSearchFailedDetail"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
