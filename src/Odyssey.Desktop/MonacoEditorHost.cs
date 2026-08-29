using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed class MonacoEditorHost : NativeWebView, ITextEditorBridge
{
    private const int MaximumBridgeMessageChars = 64 * 1024 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    public static readonly StyledProperty<TextEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<MonacoEditorHost, TextEditorViewModel?>(nameof(Editor));

    private MonacoAssetServer? _server;
    private TaskCompletionSource _editorReady = NewCompletion();
    private TaskCompletionSource<string>? _contentResponse;
    private string? _contentRequestId;
    private bool _navigationStarted;
    private bool _disposed;

    public MonacoEditorHost()
    {
        Focusable = true;
        EnvironmentRequested += ConfigureEnvironment;
        NavigationStarted += OnNavigationStarted;
        NavigationCompleted += OnNavigationCompleted;
        NewWindowRequested += OnNewWindowRequested;
        WebMessageReceived += OnWebMessageReceived;
        Unloaded += async (_, _) => await DisposeHostAsync();
    }

    public TextEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public async Task ShowDocumentAsync(
        TextEditorDocument document,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await EnsureEditorReadyAsync(cancellationToken);
        var payload = JsonSerializer.Serialize(new EditorPayload(
            document.Name, document.Content, document.LanguageId, readOnly), JsonOptions);
        await InvokeScript($"globalThis.odysseyEditor.openDocument({payload})");
    }

    public async Task<string> ReadContentAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        if (_contentResponse is not null)
            throw new InvalidOperationException("An editor content request is already running.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentRequestId = requestId;
        _contentResponse = completion;
        try
        {
            await InvokeScript($"globalThis.odysseyEditor.requestContent('{requestId}')");
            return await completion.Task.WaitAsync(StartupTimeout, cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_contentResponse, completion))
            {
                _contentResponse = null;
                _contentRequestId = null;
            }
        }
    }

    public async Task SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken = default)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        await InvokeScript($"globalThis.odysseyEditor.setReadOnly({readOnly.ToString().ToLowerInvariant()})");
    }

    public async Task MarkSavedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        await InvokeScript("globalThis.odysseyEditor.markSaved()");
    }

    public async Task FocusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEditorReadyAsync(cancellationToken);
        Focus();
        await InvokeScript("globalThis.odysseyEditor.focus()");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is TextEditorViewModel previous)
        {
            previous.PropertyChanged -= OnEditorPropertyChanged;
            previous.DetachBridge(this);
        }
        if (change.NewValue is TextEditorViewModel next)
        {
            next.PropertyChanged += OnEditorPropertyChanged;
            next.AttachBridge(this);
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TextEditorViewModel.IsOpen)
            && sender is TextEditorViewModel { IsOpen: false }
            && _editorReady.Task.IsCompletedSuccessfully)
            _ = InvokeScript("globalThis.odysseyEditor.closeDocument()");
    }

    private async Task EnsureEditorReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_navigationStarted)
        {
            _server = new MonacoAssetServer();
            _navigationStarted = true;
            Navigate(_server.EditorUri);
        }
        await _editorReady.Task.WaitAsync(StartupTimeout, cancellationToken);
    }

    private static void ConfigureEnvironment(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
        switch (args)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.IsInPrivateModeEnabled = true;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                gtk.DisableCache = true;
                break;
            case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
        }
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        if (args.Request == new Uri("about:blank")) return;
        if (_server is null || args.Request != _server.EditorUri) args.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
            _editorReady.TrySetException(new IOException("The embedded editor page could not be loaded."));
    }

    private static void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (args.Body is not { Length: > 0 } body || body.Length > MaximumBridgeMessageChars)
        {
            Editor?.ReportHostFailure("The editor bridge rejected an invalid message.");
            return;
        }
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.GetString() is not { } type) return;
            switch (type)
            {
                case "ready":
                    _editorReady.TrySetResult();
                    break;
                case "dirty" when root.TryGetProperty("dirty", out var dirty):
                    Editor?.SetDirtyFromEditor(dirty.GetBoolean());
                    break;
                case "save":
                    Editor?.RequestSaveFromEditor();
                    break;
                case "close":
                    Editor?.RequestCloseFromEditor();
                    break;
                case "content":
                    CompleteContentRequest(root);
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            Editor?.ReportHostFailure("The editor bridge returned malformed data.");
        }
    }

    private void CompleteContentRequest(JsonElement root)
    {
        if (_contentResponse is null || _contentRequestId is null
            || !root.TryGetProperty("requestId", out var requestId)
            || !string.Equals(requestId.GetString(), _contentRequestId, StringComparison.Ordinal)
            || !root.TryGetProperty("content", out var content)) return;
        _contentResponse.TrySetResult(content.GetString() ?? string.Empty);
    }

    private async Task DisposeHostAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _contentResponse?.TrySetCanceled();
        if (Editor is { } editor)
        {
            editor.PropertyChanged -= OnEditorPropertyChanged;
            editor.DetachBridge(this);
        }
        if (_server is not null) await _server.DisposeAsync();
        _server = null;
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record EditorPayload(
        string Name,
        string Content,
        string LanguageId,
        bool ReadOnly);
}
