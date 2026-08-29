using Avalonia.Controls.Documents;
using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class SyntaxHighlightingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-syntax-{Guid.NewGuid():N}");

    [Fact]
    public void CSharpScanner_ClassifiesKeywordsStringsNumbersAndComments()
    {
        const string content = "// note\npublic class Sample { string Name = \"Odyssey\"; int Count = 42; }";

        var result = new BoundedSyntaxHighlighter().Highlight("sample.cs", content);

        Assert.Equal("C#", result.Language);
        AssertSpan(result, content, "// note", QuickViewSyntaxKind.Comment);
        AssertSpan(result, content, "public", QuickViewSyntaxKind.Keyword);
        AssertSpan(result, content, "class", QuickViewSyntaxKind.Keyword);
        AssertSpan(result, content, "\"Odyssey\"", QuickViewSyntaxKind.String);
        AssertSpan(result, content, "42", QuickViewSyntaxKind.Number);
        AssertValidOrderedSpans(result, content);
    }

    [Fact]
    public void StructuredAndMarkupScanners_ClassifySemanticTokens()
    {
        const string json = "{ \"name\": \"Odyssey\", \"count\": 2, \"ready\": true }";
        const string markup = "<!-- note --><Button Content=\"Run\" />";
        var highlighter = new BoundedSyntaxHighlighter();

        var jsonResult = highlighter.Highlight("settings.json", json);
        var markupResult = highlighter.Highlight("View.axaml", markup);

        AssertSpan(jsonResult, json, "\"name\"", QuickViewSyntaxKind.Property);
        AssertSpan(jsonResult, json, "\"Odyssey\"", QuickViewSyntaxKind.String);
        AssertSpan(jsonResult, json, "true", QuickViewSyntaxKind.Keyword);
        AssertSpan(markupResult, markup, "<!-- note -->", QuickViewSyntaxKind.Comment);
        AssertSpan(markupResult, markup, "Button", QuickViewSyntaxKind.Tag);
        AssertSpan(markupResult, markup, "Content", QuickViewSyntaxKind.Attribute);
        AssertSpan(markupResult, markup, "\"Run\"", QuickViewSyntaxKind.String);
        AssertValidOrderedSpans(jsonResult, json);
        AssertValidOrderedSpans(markupResult, markup);
    }

    [Theory]
    [InlineData("script.py", "# note\ndef run():\n    return 'ok'", "Python")]
    [InlineData("deploy.sh", "if true; then echo \"ok\"; fi", "Shell")]
    [InlineData("build.ps1", "$value = 12 # note", "PowerShell")]
    [InlineData("query.sql", "SELECT name FROM files WHERE size > 10", "SQL")]
    [InlineData("style.css", "/* note */ .file { color: red; width: 12px; }", "CSS")]
    [InlineData("config.yaml", "enabled: true # note", "YAML")]
    [InlineData("config.toml", "enabled = true # note", "TOML")]
    [InlineData("README.md", "# Heading\nUse `F3` and [docs](quick-view.md).", "Markdown")]
    [InlineData("main.rs", "pub fn main() { let value = 1; }", "Rust")]
    [InlineData("main.go", "package main\nfunc main() { return }", "Go")]
    public void CommonFormats_AreDetectedWithoutRegex(string path, string content, string language)
    {
        var result = new BoundedSyntaxHighlighter().Highlight(path, content);

        Assert.Equal(language, result.Language);
        Assert.NotEmpty(result.Spans);
        AssertValidOrderedSpans(result, content);
    }

    [Fact]
    public void SpanLimitAndCancellationAreEnforced()
    {
        var content = string.Join(' ', Enumerable.Repeat("public 123 \"value\"", 100));
        var bounded = new BoundedSyntaxHighlighter(8).Highlight("large.cs", content);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(8, bounded.Spans.Count);
        Assert.True(bounded.IsTruncated);
        Assert.Throws<OperationCanceledException>(() =>
            new BoundedSyntaxHighlighter().Highlight("large.cs", content, cancellation.Token));
        AssertValidOrderedSpans(bounded, content);
    }

    [Fact]
    public async Task QuickView_HighlightsOnlyTextForKnownExtensions()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.cs");
        await File.WriteAllTextAsync(path, "public class Sample { int Count = 42; }");
        var service = new QuickViewService();

        var text = await service.ReadAsync(new QuickViewReadRequest { Path = path });
        var hex = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path,
            Mode = QuickViewDisplayMode.Hex
        });

        Assert.Equal("C#", text.SyntaxLanguage);
        Assert.NotEmpty(text.SyntaxSpans);
        Assert.Empty(hex.SyntaxSpans);
        Assert.Null(hex.SyntaxLanguage);
    }

    [Fact]
    public async Task ViewModel_ExposesSelectableSyntaxPresentationAndLocalizedStatus()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.py");
        await File.WriteAllTextAsync(path, "def run():\n    return \"ok\"");
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var localization = new LocalizationService(storage);
        localization.SelectedLanguage = localization.Languages.Single(language => language.Code == "en");
        var viewModel = new QuickViewViewModel(
            new QuickViewService(), localization);

        await viewModel.OpenAsync(path);

        Assert.True(viewModel.HasSyntaxHighlighting);
        Assert.False(viewModel.ShowPlainContent);
        Assert.StartsWith("Syntax: Python", viewModel.SyntaxStatus, StringComparison.Ordinal);
        var viewer = new SyntaxTextViewer
        {
            ContentText = viewModel.Content,
            SyntaxSpans = viewModel.SyntaxSpans
        };
        Assert.Equal(viewModel.Content,
            string.Concat(viewer.Inlines!.OfType<Run>().Select(run => run.Text)));
        viewModel.Close();
    }

    [Fact]
    public void UnknownExtension_RemainsPlainText()
    {
        var result = new BoundedSyntaxHighlighter().Highlight("value.unknown", "public class Value");

        Assert.Null(result.Language);
        Assert.Empty(result.Spans);
    }

    private static void AssertSpan(
        QuickViewSyntaxResult result,
        string content,
        string expected,
        QuickViewSyntaxKind kind) =>
        Assert.Contains(result.Spans, span => span.Kind == kind
                                             && content.Substring(span.Start, span.Length) == expected);

    private static void AssertValidOrderedSpans(QuickViewSyntaxResult result, string content)
    {
        var completed = 0;
        foreach (var span in result.Spans)
        {
            Assert.InRange(span.Start, completed, content.Length);
            Assert.InRange(span.Length, 1, content.Length - span.Start);
            completed = span.Start + span.Length;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
