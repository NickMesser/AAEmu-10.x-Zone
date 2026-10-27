using System.Numerics;
using System.Text;

namespace AAEmu.Zone.Ai;

/// <summary>Value-owned copy of the native AISignalExtraData fields; no engine pointers escape an actor.</summary>
public sealed record ZoneAiSignalData(Vector3 Point = default, Vector3 Point2 = default, ulong EntityId = 0,
    float FloatValue = 0, int Value = 0, int Value2 = 0, string ObjectName = "");

public sealed record ZoneAiSignal(int Kind, string Name, uint Crc, uint SenderEntityId, ZoneAiSignalData? Data);
public sealed record ZoneAiAuxiliaryRequest(int Kind, string Name, float Delay, int Priority);
public readonly record struct ZoneAiAuxiliaryState(string Name, float RemainingDelay, int Kind, int Priority,
    bool Started, int IgnoredRequests);

internal static class ZoneAiSignalName
{
    internal static string CString(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var end = name.IndexOf('\0');
        return end < 0 ? name : name[..end];
    }

    internal static uint Crc(string name)
    {
        // xlcommon Crc32Gen: reflected IEEE polynomial, initial and final complement.
        var crc = uint.MaxValue;
        foreach (var value in Encoding.UTF8.GetBytes(name))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }

    internal static bool IsConstructor(string name)
    {
        const string expected = "constructor";
        if (name.Length != expected.Length) return false;
        for (var i = 0; i < name.Length; i++)
            if ((name[i] | 0x20) != expected[i]) return false;
        return true;
    }
}
