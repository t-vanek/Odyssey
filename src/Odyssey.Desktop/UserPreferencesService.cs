using System.Text.Json;
using Odyssey.Infrastructure;

namespace Odyssey.Desktop;

public sealed class UserPreferencesService
{
    private readonly string _path;
    private readonly object _sync = new();
    private Preferences _preferences;

    public UserPreferencesService(ApplicationStorage storage)
    {
        _path = Path.Combine(storage.DirectoryPath, "preferences.json");
        _preferences = Load();
    }

    public bool ReadOnlyMode
    {
        get => _preferences.ReadOnlyMode;
        set { _preferences = _preferences with { ReadOnlyMode = value }; Save(); }
    }

    public bool AutomaticDrives
    {
        get => _preferences.AutomaticDrives;
        set { _preferences = _preferences with { AutomaticDrives = value }; Save(); }
    }

    public FilePaneWorkspaceSnapshot? LeftPaneWorkspace => _preferences.LeftPaneWorkspace;
    public FilePaneWorkspaceSnapshot? RightPaneWorkspace => _preferences.RightPaneWorkspace;
    public IReadOnlyList<string> Hotlist => _preferences.Hotlist ?? [];

    public void SaveCommanderWorkspace(
        FilePaneWorkspaceSnapshot left,
        FilePaneWorkspaceSnapshot right,
        IEnumerable<string> hotlist)
    {
        var paths = hotlist.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(PathComparer).Take(256).ToArray();
        _preferences = _preferences with
        {
            LeftPaneWorkspace = left,
            RightPaneWorkspace = right,
            Hotlist = paths
        };
        Save();
    }

    private Preferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Preferences();
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path)) ?? new Preferences();
        }
        catch { return new Preferences(); }
    }

    private void Save()
    {
        try
        {
            lock (_sync)
            {
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_preferences));
                File.Move(temporaryPath, _path, overwrite: true);
            }
        }
        catch { /* Preferences must not prevent Odyssey from running. */ }
    }

    private sealed record Preferences(
        bool ReadOnlyMode = true,
        bool AutomaticDrives = true,
        FilePaneWorkspaceSnapshot? LeftPaneWorkspace = null,
        FilePaneWorkspaceSnapshot? RightPaneWorkspace = null,
        string[]? Hotlist = null);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
