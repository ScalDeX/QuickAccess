namespace QuickAccess;

public record ModuleDef(string Id, string NameKey, string DescKey);

public static class ModuleRegistry
{
    public static readonly List<ModuleDef> All = new()
    {
        new ModuleDef("clock", "mod.clockN", "mod.clockD"),
        new ModuleDef("spring", "mod.springN", "mod.springD"),
    };

    public static bool IsEnabled(AppSettings settings, string id)
    {
        if (settings.ModuleStates != null && settings.ModuleStates.TryGetValue(id, out var v))
            return v;
        return true;
    }

    public static void SetEnabled(AppSettings settings, string id, bool enabled)
    {
        settings.ModuleStates ??= new();
        settings.ModuleStates[id] = enabled;
    }
}
