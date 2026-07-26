using System.Text.Json;

namespace Winstaller.Utilities;

public sealed class SetupTaskStateStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winstaller", "state.json");
    public bool IsComplete(string task) => Load().Completed.ContainsKey(task);
    public void Complete(string task) { var state = Load(); state.Completed[task] = DateTimeOffset.UtcNow; Save(state); }
    public void Clear(string task) { var state = Load(); state.Completed.Remove(task); Save(state); }
    private State Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new() : new(); } catch { return new(); }
    }
    private void Save(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", true); else File.Move(temp, _path);
    }
    private sealed class State { public Dictionary<string, DateTimeOffset> Completed { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
}
