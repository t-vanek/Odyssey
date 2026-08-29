using System.Text.Json;
using Odyssey.Infrastructure;

namespace Odyssey.Agent;

public sealed class AgentAccessPolicyService
{
    private readonly string _path;
    private readonly object _gate = new();

    public AgentAccessPolicyService(ApplicationStorage storage) =>
        _path = Path.Combine(storage.DirectoryPath, "agent-policy.json");

    public event EventHandler? Changed;

    public AgentAccessLevel AccessLevel
    {
        get { lock (_gate) return Load().AccessLevel; }
        set
        {
            lock (_gate) Save(new AgentPolicy(value));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private AgentPolicy Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AgentPolicy();
            return JsonSerializer.Deserialize<AgentPolicy>(File.ReadAllText(_path)) ?? new AgentPolicy();
        }
        catch { return new AgentPolicy(); }
    }

    private void Save(AgentPolicy policy)
    {
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(policy));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private sealed record AgentPolicy(AgentAccessLevel AccessLevel = AgentAccessLevel.ReadOnly);
}
