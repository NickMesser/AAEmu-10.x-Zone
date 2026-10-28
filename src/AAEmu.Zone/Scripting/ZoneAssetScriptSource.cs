using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Reads original script bytes through the scene's existing loose/archive asset order. Script
/// cache spelling stays with ZoneScriptLoader; the asset boundary uses lowercase game paths.
/// </summary>
public sealed class ZoneAssetScriptSource(IZoneAssetSource assets) : IZoneScriptSource
{
    public const int MaximumScriptBytes = 16 * 1024 * 1024;
    private readonly IZoneAssetSource _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    public ZoneScriptFile? Read(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var path = ZoneAssetPath.Validate(filename).ToCharArray();
        for (var i = 0; i < path.Length; i++)
            if (path[i] is >= 'A' and <= 'Z') path[i] += (char)('a' - 'A');
        var normalized = new string(path);
        if (normalized.StartsWith("game/", StringComparison.Ordinal)) normalized = normalized[5..];
        if (!normalized.StartsWith("scripts/", StringComparison.Ordinal) &&
            !normalized.StartsWith("scriptsbin64/", StringComparison.Ordinal))
            throw new IOException("Script assets must be within game/scripts or game/scriptsbin64.");
        normalized = "game/" + normalized;
        using var stream = _assets.OpenRead(normalized);
        if (stream is null) return null;
        using var bytes = new MemoryStream();
        Span<byte> buffer = stackalloc byte[8192];
        while (true)
        {
            var count = stream.Read(buffer);
            if (count == 0) break;
            if (bytes.Length + count > MaximumScriptBytes)
                throw new InvalidDataException($"Script asset '{normalized}' exceeds {MaximumScriptBytes} bytes.");
            bytes.Write(buffer[..count]);
        }
        return new(bytes.ToArray(), normalized);
    }
}
