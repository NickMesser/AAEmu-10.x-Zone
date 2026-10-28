using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    /// <summary>The environment slot that holds the CAISystem instance (gEnv+0x80 == CSystem+0xD0).</summary>
    internal const int AiSystemEnvironmentOffset = 0x80;

    /// <summary>
    /// Byte offset of CAISystem vtable slot 3, which holds the AI init function 31049268 that
    /// framework PostInit runs (FIRST_ENTRY_NOTES.md:722-724). That slot holding exactly this
    /// function identifies the object as the CAISystem rather than any other object whose vtable
    /// lies in cryaisystem_dedicate.dll.
    /// </summary>
    private const int AiSystemInitSlot = 3 * PointerSize;
    private const int AiSystemInitRva = 0x49268;

    /// <summary>
    /// Reads the CAISystem instance from the engine environment after framework init, refusing by
    /// name when it is not proven. Evidence (live --check-framework run, post-PostInit scan of every
    /// environment slot for pointers whose vtable lies in a loaded engine image): env+0x80 is the only
    /// slot whose vtable is in cryaisystem_dedicate.dll (image 0x31000000, size 0x220000; vtable
    /// 0x311DDA00 = RVA 0x1DDA00), and that vtable's slot 3 is 0x31049268, the very "AI init"
    /// function PostInit calls. The neighbours are other services: env+0x88 is the EntitySystem
    /// (Native/Engine/ZoneNativeEngine.Entities.cs:21) and env+0xA8 the character manager (:68),
    /// whose vtables lie in cryentitysystem_dedicate.dll and cryanimation.dll. RTTI is not used for
    /// identity; the slot-3 init address is.
    /// </summary>
    internal bool TryReadAiSystem(out nint instance, out string detail)
    {
        instance = 0;
        VerifyAccess();
        if (!_ready || _disposed || _disposing || _system == 0 || _environment != _system + 0x50)
        {
            detail = "the engine is not ready or its environment is not CSystem+0x50";
            return false;
        }
        var module = GetModuleHandleW("cryaisystem_dedicate.dll");
        if (module == 0 || !_admittedModules.Contains("cryaisystem_dedicate.dll"))
        {
            detail = "cryaisystem_dedicate.dll is not a loaded, admitted module";
            return false;
        }
        var candidate = Marshal.ReadIntPtr(_environment + AiSystemEnvironmentOffset);
        if (candidate == 0)
        {
            detail = $"gEnv+0x{AiSystemEnvironmentOffset:X} is null (AI init did not publish a CAISystem)";
            return false;
        }
        var imageSize = Marshal.ReadInt32(module + Marshal.ReadInt32(module + 0x3c) + 0x18 + 56); // OptionalHeader.SizeOfImage
        var vtable = Marshal.ReadIntPtr(candidate);
        if (vtable < module || vtable >= module + imageSize)
        {
            detail = $"gEnv+0x{AiSystemEnvironmentOffset:X} vtable 0x{vtable:X} lies outside cryaisystem_dedicate.dll";
            return false;
        }
        var init = Marshal.ReadIntPtr(vtable + AiSystemInitSlot);
        if (init != module + AiSystemInitRva)
        {
            detail = $"gEnv+0x{AiSystemEnvironmentOffset:X} vtable slot 3 is 0x{init:X}, not the AI init function 0x{module + AiSystemInitRva:X}";
            return false;
        }
        instance = candidate;
        detail = $"gEnv+0x{AiSystemEnvironmentOffset:X} = 0x{candidate:X}, vtable 0x{vtable:X} in cryaisystem_dedicate.dll, slot 3 == AI init 0x{init:X}";
        return true;
    }
}
