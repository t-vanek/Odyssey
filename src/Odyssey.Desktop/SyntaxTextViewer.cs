using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed class SyntaxTextViewer : SelectableTextBlock
{
    public static readonly StyledProperty<string> ContentTextProperty =
        AvaloniaProperty.Register<SyntaxTextViewer, string>(nameof(ContentText), string.Empty);
    public static readonly StyledProperty<IReadOnlyList<QuickViewSyntaxSpan>> SyntaxSpansProperty =
        AvaloniaProperty.Register<SyntaxTextViewer, IReadOnlyList<QuickViewSyntaxSpan>>(
            nameof(SyntaxSpans), Array.Empty<QuickViewSyntaxSpan>());

    private static readonly IReadOnlyDictionary<QuickViewSyntaxKind, IBrush> BrushesByKind =
        new Dictionary<QuickViewSyntaxKind, IBrush>
        {
            [QuickViewSyntaxKind.Keyword] = Brush("#C792EA"),
            [QuickViewSyntaxKind.String] = Brush("#C3E88D"),
            [QuickViewSyntaxKind.Number] = Brush("#F78C6C"),
            [QuickViewSyntaxKind.Comment] = Brush("#71808F"),
            [QuickViewSyntaxKind.Property] = Brush("#82AAFF"),
            [QuickViewSyntaxKind.Tag] = Brush("#F07178"),
            [QuickViewSyntaxKind.Attribute] = Brush("#FFCB6B"),
            [QuickViewSyntaxKind.Heading] = Brush("#89DDFF"),
            [QuickViewSyntaxKind.Link] = Brush("#80CBC4")
        };

    public string ContentText
    {
        get => GetValue(ContentTextProperty);
        set => SetValue(ContentTextProperty, value);
    }

    public IReadOnlyList<QuickViewSyntaxSpan> SyntaxSpans
    {
        get => GetValue(SyntaxSpansProperty);
        set => SetValue(SyntaxSpansProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentTextProperty || change.Property == SyntaxSpansProperty)
            RebuildInlines();
    }

    private void RebuildInlines()
    {
        var content = ContentText ?? string.Empty;
        var inlines = new InlineCollection();
        var completed = 0;
        foreach (var span in SyntaxSpans.Take(4096))
        {
            if (span.Start < completed || span.Length <= 0 || span.Start > content.Length
                || span.Length > content.Length - span.Start) continue;
            if (span.Start > completed) inlines.Add(content[completed..span.Start]);
            var run = new Run(content.Substring(span.Start, span.Length));
            if (BrushesByKind.TryGetValue(span.Kind, out var brush)) run.Foreground = brush;
            inlines.Add(run);
            completed = span.Start + span.Length;
        }
        if (completed < content.Length) inlines.Add(content[completed..]);
        Inlines = inlines;
    }

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}
