using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed class FileEntryIconConverter : IValueConverter
{
    public static FileEntryIconConverter Instance { get; } = new();

    private static readonly Geometry File = Geometry.Parse("M6 2H14L19 7V22H6ZM14 4.8V8H17.2ZM9 12H16V14H9ZM9 16H16V18H9Z");
    private static readonly Geometry Folder = Geometry.Parse("M3 5H10L12 7H21V19H3ZM5 9V17H19V9Z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileEntryType.Directory ? Folder : File;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
