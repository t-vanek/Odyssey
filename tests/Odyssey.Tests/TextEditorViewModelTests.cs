using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class TextEditorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-editor-vm-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadOnlyDocumentCannotSaveUntilManagementModeIsEnabled()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "document.cs");
        await File.WriteAllTextAsync(path, "class Original { }");
        var desktop = new FakeDesktopInteraction();
        var bridge = new FakeEditorBridge { Content = "class Changed { }" };
        var viewModel = ViewModel(desktop);
        viewModel.AttachBridge(bridge);

        await viewModel.OpenAsync(path);
        viewModel.SetDirtyFromEditor(true);

        Assert.True(viewModel.IsReadOnly);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.True(bridge.LastReadOnly);

        viewModel.AccessMode = FileAccessMode.ManageFiles;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDirty);
        Assert.True(bridge.MarkSavedCalled);
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(path));
        Assert.Contains("verified", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveConflictRemainsDirtyAndNeverOverwritesExternalChange()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "conflict.txt");
        await File.WriteAllTextAsync(path, "original");
        var bridge = new FakeEditorBridge { Content = "editor" };
        var viewModel = ViewModel(new FakeDesktopInteraction());
        viewModel.AttachBridge(bridge);
        viewModel.AccessMode = FileAccessMode.ManageFiles;
        await viewModel.OpenAsync(path);
        viewModel.SetDirtyFromEditor(true);
        await File.WriteAllTextAsync(path, "external");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.HasError);
        Assert.False(bridge.MarkSavedCalled);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DirtyCloseRequiresExplicitDiscardConfirmation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "close.txt");
        await File.WriteAllTextAsync(path, "original");
        var desktop = new FakeDesktopInteraction { Confirmation = false };
        var viewModel = ViewModel(desktop);
        viewModel.AttachBridge(new FakeEditorBridge());
        await viewModel.OpenAsync(path);
        viewModel.SetDirtyFromEditor(true);

        await viewModel.CloseCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsOpen);
        Assert.Equal(1, desktop.ConfirmationCount);

        desktop.Confirmation = true;
        await viewModel.CloseCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsOpen);
        Assert.Null(viewModel.Document);
        Assert.Equal(2, desktop.ConfirmationCount);
    }

    [Fact]
    public async Task MissingWebViewRuntimeUsesFallbackWithoutCreatingNativeControl()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "fallback.txt");
        await File.WriteAllTextAsync(path, "content");
        var desktop = new FakeDesktopInteraction();
        var viewModel = ViewModel(desktop);
        var probeCount = 0;
        var host = new MonacoEditorHost(() =>
        {
            probeCount++;
            return "WebKit test runtime is unavailable.";
        })
        {
            Editor = viewModel
        };

        await viewModel.OpenAsync(path);

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.HasError);
        Assert.True(viewModel.IsHostUnavailable);
        Assert.False(viewModel.IsHostAvailable);
        Assert.False(viewModel.HasOperationalError);
        Assert.Contains("WebKit test runtime is unavailable", viewModel.Error, StringComparison.Ordinal);
        Assert.Equal("WebKit test runtime is unavailable.", viewModel.HostFailureDetails);
        Assert.False(host.HasNativeWebView);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        viewModel.ToggleHostDetailsCommand.Execute(null);
        Assert.True(viewModel.AreHostDetailsVisible);
        Assert.Equal("Hide technical details", viewModel.HostDetailsButtonText);

        viewModel.RetryHostCommand.Execute(null);
        Assert.False(viewModel.AreHostDetailsVisible);
        Assert.Equal(2, probeCount);
        Assert.True(viewModel.IsHostUnavailable);

        await viewModel.OpenExternalCommand.ExecuteAsync(null);
        Assert.Equal(path, desktop.LastEditedPath);
    }

    private TextEditorViewModel ViewModel(FakeDesktopInteraction desktop)
    {
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var localization = new LocalizationService(storage);
        localization.SelectedLanguage = localization.Languages.Single(language => language.Code == "en");
        return new TextEditorViewModel(new SafeTextEditorService(), desktop, localization);
    }

    private sealed class FakeEditorBridge : ITextEditorBridge
    {
        public string Content { get; init; } = "original";
        public bool LastReadOnly { get; private set; }
        public bool MarkSavedCalled { get; private set; }

        public Task ShowDocumentAsync(
            TextEditorDocument document,
            bool readOnly,
            CancellationToken cancellationToken = default)
        {
            LastReadOnly = readOnly;
            return Task.CompletedTask;
        }

        public Task<string> ReadContentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Content);

        public Task SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken = default)
        {
            LastReadOnly = readOnly;
            return Task.CompletedTask;
        }

        public Task MarkSavedAsync(CancellationToken cancellationToken = default)
        {
            MarkSavedCalled = true;
            return Task.CompletedTask;
        }

        public Task FocusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDesktopInteraction : IDesktopInteractionService
    {
        public bool Confirmation { get; set; }
        public int ConfirmationCount { get; private set; }
        public string? LastEditedPath { get; private set; }
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickDestinationFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialValue = "") =>
            Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            ConfirmationCount++;
            return Task.FromResult(Confirmation);
        }
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenAsync(string path) => Task.CompletedTask;
        public Task EditAsync(string path)
        {
            LastEditedPath = path;
            return Task.CompletedTask;
        }
        public Task OpenContainingFolderAsync(string path) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
