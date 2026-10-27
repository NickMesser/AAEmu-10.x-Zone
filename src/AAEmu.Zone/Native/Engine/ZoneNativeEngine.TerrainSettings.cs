using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.Engine;

/// <summary>Actual initialized native configuration values, not registration defaults.</summary>
public sealed record ZoneNativeTerrainSettings(int TexturePoolSize, int TextureSyncLoad);

public sealed partial class ZoneNativeEngine
{
    /// <summary>
    /// Captures e_terrain_texture_buffers and e_terrain_texture_sync_load from this engine's
    /// initialized 3DEngine configuration. The copied settings retain no native resource.
    /// A frame/precache producer can request synchronous loading separately from this raw value.
    /// </summary>
    public ZoneNativeTerrainSettings CaptureTerrainSettings()
    {
        using var operation = EnterOperation();
        int poolSize, syncLoad;
        try
        {
            var module = RequiredModule("cry3dengine_dedicate.dll");
            var terrainEngine = Marshal.ReadIntPtr(module + 0x2fa8c8);
            if (Marshal.ReadIntPtr(module + 0x33f588) != _environment || terrainEngine == 0 ||
                Marshal.ReadIntPtr(_environment + 0x58) != terrainEngine)
                throw new InvalidOperationException("The terrain configuration does not belong to this engine's 3DEngine service.");
            var configuration = Marshal.ReadIntPtr(module + 0x2fa8d0);
            if (configuration == 0)
                throw new InvalidOperationException("The native terrain configuration has not been initialized.");
            // 3000DF95 binds these storage addresses through the real engine console. Its
            // registration defaults (512 and0) need not be the effective values captured here.
            poolSize = Marshal.ReadInt32(configuration + 0x1e4);
            syncLoad = Marshal.ReadInt32(configuration + 0x1f0);
        }
        catch (Exception error) { Poison(error); throw; }
        // An unsupported configured allocation size is recoverable admission, not a native
        // lifetime failure. The caller can still retire the healthy engine normally.
        if (poolSize < 0)
            throw new InvalidDataException("The native terrain texture pool size cannot back a managed allocation.");
        return new(poolSize, syncLoad);
    }
}
