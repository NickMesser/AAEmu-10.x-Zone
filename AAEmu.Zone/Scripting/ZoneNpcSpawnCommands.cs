using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Scripting;

/// <summary>The type2 and type5 command-set forms emitted by39341BD0/39341E00 through3933D790.</summary>
internal static class ZoneNpcSpawnCommands
{
    internal static void Run(ZoneLuaReference entity, bool followPath, string path, uint pathType,
        Func<bool> isCurrent, Action<string> diagnostic)
    {
        var lua = entity.Runtime;
        if (!isCurrent() || entity.IsDisposed) return;
        try
        {
            using var ai = lua.Get(entity, "AI", 5) as ZoneLuaReference;
            if (!isCurrent()) return;
            if (ai is null) { diagnostic("The NPC entity has no AI script table."); return; }
            using var commandSet = lua.Get(ai, "commandSet", 5) as ZoneLuaReference;
            if (!isCurrent()) return;
            if (commandSet is null) { diagnostic("The NPC AI has no commandSet script table."); return; }
            using var run = lua.Get(commandSet, "RunCommandSet", 6) as ZoneLuaReference;
            if (!isCurrent()) return;
            if (run is null) { diagnostic("The NPC commandSet has no RunCommandSet function."); return; }
            using var request = lua.CreateTable();
            using var commands = lua.CreateTable();
            using var command = lua.CreateTable();
            request.Set("commands", commands);
            request.Set("commanderUnit", null);
            request.Set("runType", 1f);
            command.Set(1, followPath ? 2f : 5f);
            if (followPath)
            {
                //39341BD0 formats the32-bit path type with%d;3933D790 parses that signed text
                // and converts the integer to float before handing it to the script system.
                command.Set(2, (float)unchecked((int)pathType));
                command.Set(3, ZoneAiSignalName.CString(path));
            }
            commands.Set(1, command);
            request.Set("canInteract", followPath);
            if (isCurrent()) lua.Invoke(run, commandSet, entity, request);
        }
        catch (ZoneLuaException error)
        {
            if (isCurrent()) diagnostic($"RunCommandSet failed: {error.Message}");
        }
    }
}
