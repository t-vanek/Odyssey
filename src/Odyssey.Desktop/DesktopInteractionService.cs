using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Odyssey.Desktop;

public interface IDesktopInteractionService
{
    Task<string?> PickFolderAsync();
    Task<string?> PickDestinationFolderAsync();
    Task<string?> PromptTextAsync(string title, string message, string initialValue = "");
    Task<string?> PromptMultilineAsync(string title, string message, string initialValue = "") =>
        PromptTextAsync(title, message, initialValue);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task CopyTextAsync(string text);
    Task OpenAsync(string path);
    Task EditAsync(string path);
    Task OpenContainingFolderAsync(string path);
}

public sealed class DesktopInteractionService(LocalizationService localization) : IDesktopInteractionService
{
    private Window? _window;
    public void Attach(Window window) => _window = window;

    public async Task<string?> PickFolderAsync()
    {
        var folders = await RequireWindow().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = localization["PickFolderTitle"],
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickDestinationFolderAsync()
    {
        var folders = await RequireWindow().StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = localization["PickDestinationTitle"],
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initialValue = "")
    {
        string? result = null;
        var input = new TextBox { Text = initialValue, MinWidth = 390 };
        var ok = new Button { Content = localization["Confirm"], IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = localization["Cancel"], IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, ok }
        };
        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 10,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, input, buttons }
        };
        var dialog = CreateDialog(title, content);
        ok.Click += (_, _) => { result = input.Text?.Trim(); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(RequireWindow());
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = false;
        var ok = new Button { Content = confirmText, IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = localization["Cancel"], IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, ok }
        };
        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 12,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 480 }, buttons }
        };
        var dialog = CreateDialog(title, content);
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(RequireWindow());
        return result;
    }

    public async Task<string?> PromptMultilineAsync(string title, string message, string initialValue = "")
    {
        string? result = null;
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 640,
            MinHeight = 280,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = "monospace"
        };
        ScrollViewer.SetVerticalScrollBarVisibility(input, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(input, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var ok = new Button { Content = localization["Confirm"], IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = localization["Cancel"], IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, ok }
        };
        var content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 10,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, input, buttons }
        };
        var dialog = CreateDialog(title, content);
        dialog.Width = 720;
        ok.Click += (_, _) => { result = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(RequireWindow());
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = RequireWindow().Clipboard ?? throw new InvalidOperationException(localization["ClipboardUnavailable"]);
        await clipboard.SetTextAsync(text);
    }

    public Task OpenAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(localization["EntryUnavailable"], path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task EditAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(localization["EntryUnavailable"], path);
        var startInfo = new ProcessStartInfo(path) { UseShellExecute = true };
        if (OperatingSystem.IsWindows()) startInfo.Verb = "edit";
        try
        {
            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsWindows())
        {
            // Some Windows file associations expose only the default open verb.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    public Task OpenContainingFolderAsync(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException(localization["FolderUnavailable"]);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private Window RequireWindow() => _window ?? throw new InvalidOperationException("The desktop window is not ready.");

    private static Window CreateDialog(string title, Control content) => new()
    {
        Title = title,
        Width = 520,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = content
    };
}
