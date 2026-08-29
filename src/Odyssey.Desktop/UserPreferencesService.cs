using System.Text.Json;
using Odyssey.Infrastructure;

namespace Odyssey.Desktop;

public sealed class UserPreferencesService
{
    private readonly string _path;
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
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_preferences)); }
        catch { /* Preferences must not prevent Odyssey from running. */ }
    }

    private sealed record Preferences(bool ReadOnlyMode = true, bool AutomaticDrives = true);
}
