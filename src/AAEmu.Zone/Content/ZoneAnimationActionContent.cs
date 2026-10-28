using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

/// <summary>The native animation-action ID and descriptor state. Signal/mount payloads remain separate consumers.</summary>
public sealed record ZoneAnimationActionDefinition(uint Id, int ActionState);

/// <summary>
/// Actual anim_actions metadata used by3945DDB0/39BFE4A0. Native loader39C04BB0 reads
/// action_state_id into descriptor0. This owns no graph, signal, equipment or mount effects.
/// </summary>
public sealed class ZoneAnimationActionContent
{
    public IReadOnlyDictionary<uint, ZoneAnimationActionDefinition> Actions { get; }
    public ZoneAnimationActionContent(IEnumerable<ZoneAnimationActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Actions = actions.ToFrozenDictionary(action => action.Id);
    }

    internal static ZoneAnimationActionContent Load(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, action_state_id FROM anim_actions";
        using var reader = command.ExecuteReader();
        var actions = new List<ZoneAnimationActionDefinition>();
        while (reader.Read()) actions.Add(new(NativeContentReader.UInt32(reader, 0), NativeContentReader.Int32(reader, 1)));
        return new(actions);
    }
}
