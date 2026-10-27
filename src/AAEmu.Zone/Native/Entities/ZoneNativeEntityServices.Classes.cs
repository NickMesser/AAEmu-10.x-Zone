using System.Runtime.InteropServices;
using System.Text;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.Entities;

public sealed unsafe partial class ZoneNativeEntityServices
{
    private readonly List<CellEntityClasses> _cellEntityClasses = [];
    private CellEntityClasses? _cellClassLoadScope;

    // CEntitySystem Init3201EEB0 ->32094D70 has already populated the real registry from
    // Entities/*.ent. This is identity/capability admission only:32097000 must execute Lua
    // lazily after its own MinSpec, class, archetype and BeforeSpawn gates, in native row order.
    private void AcquireCellEntityClasses(ZoneCellEntityContent content, out CellEntityClasses token)
    {
        _lease.RequireActiveOperation();
        ArgumentNullException.ThrowIfNull(content);
        var retained = new CellEntityClasses(this);
        token = retained;
        _cellEntityClasses.Add(retained); // Caller retains even partial admission before native calls.
        ValidateCellClassRegistry();
        retained.HasMinimumSpec = content.Entities.Any(row => row.MinimumSpec > 0);
        if (retained.HasMinimumSpec)
        {
            var quality = ReadCellEntityQuality();
            retained.QualityVariable = quality.Pointer;
            retained.QualityTable = quality.Table;
            retained.CurrentSpec = quality.Value;
        }
        var rows = content.Entities.Where(row => row.PassesMinimumSpec(retained.CurrentSpec)).ToArray();
        CaptureCellFlowGraphPrerequisite(retained, rows);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!names.Add(row.ClassName)) continue;
            ValidateString(row.ClassName, nameof(content));
            var item = new CellEntityClass(row.ClassName);
            retained.Classes.Add(item);
            var bytes = Encoding.UTF8.GetBytes(row.ClassName + '\0');
            item.NameInput = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, item.NameInput, bytes.Length);
            item.Pointer = LookupCellClass(item);
            if (item.Pointer == 0)
                throw new NotSupportedException($"The native registry has no authored entity class '{row.ClassName}'.");
            RequireTable(item.Pointer, 0xd9298);
            item.Flags = unchecked((uint)Marshal.ReadInt32(item.Pointer + 8));
            item.NativeName = ReadCellClassString(Marshal.ReadIntPtr(item.Pointer + 0x10));
            item.ScriptPath = ReadCellClassString(Marshal.ReadIntPtr(item.Pointer + 0x18));
            item.Script = Marshal.ReadIntPtr(item.Pointer + 0x20);
            Require(item.NativeName == item.Name, "The native registry resolved a different authored class name.");
            if (Marshal.ReadIntPtr(item.Pointer + 0x28) != 0 || Marshal.ReadIntPtr(item.Pointer + 0x30) != 0)
                throw new NotSupportedException($"Entity class '{item.Name}' requires its native custom creator owner.");
            if (item.Script == 0)
            {
                Require(item.ScriptPath.Length == 0, "An authored script-backed class has no native script owner.");
                continue; // Actual scriptless AreaShape remains its registered native class.
            }
            Require(item.ScriptPath.Length != 0, "A native class script owner has no authored script path.");
            if (retained.ScriptSystem == 0) CaptureCellClassServices(retained);
            ValidateCellClassScript(retained, item);
            CaptureCellClassPrototype(item);
        }
        retained.Prepared = true;
        ValidateCellEntityClasses(retained);
    }

    // The batch owner brackets native +120 and later script-capable completion callbacks.
    // Capture newly registered entities before Complete, so a validation failure retains children.
    private void BeginCellEntityClassLoads(CellEntityClasses token)
    {
        _lease.RequireActiveOperation();
        if (_cellClassLoadScope is not null)
            throw new InvalidOperationException("Native cell class loading cannot overlap another callback scope.");
        ValidateCellEntityClasses(token);
        foreach (var held in _cellEntityClasses.Where(held => held.Prepared && !ReferenceEquals(held, token)))
            ValidateCellClassRecords(held, captureNewPrototype: false);
        _cellClassLoadScope = token;
    }

    private void CompleteCellEntityClassLoads(CellEntityClasses token)
    {
        _lease.RequireActiveOperation();
        Require(ReferenceEquals(_cellClassLoadScope, token), "The native class-loading callback scope changed.");
        RequireCellClassToken(token, prepared: true);
        ValidateCellClassRegistry();
        // Another cell can share an initially unloaded class. Record the actual lazy transition
        // in every borrowing token; already loaded prototypes must keep their exact identities.
        foreach (var held in _cellEntityClasses.Where(held => held.Prepared))
            ValidateCellClassRecords(held, captureNewPrototype: true);
        ValidateCellClassAdmission(token);
        _cellClassLoadScope = null;
    }

    private void ValidateCellEntityClasses(CellEntityClasses token)
    {
        _lease.RequireActiveOperation();
        RequireCellClassToken(token, prepared: true);
        Require(_cellClassLoadScope is null, "Native class caches are still inside a callback scope.");
        ValidateCellClassRegistry();
        ValidateCellClassAdmission(token);
        ValidateCellClassRecords(token, captureNewPrototype: false);
    }

    private void ValidateCellClassRecords(CellEntityClasses token, bool captureNewPrototype)
    {
        if (token.ScriptSystem != 0) ValidateCellClassServices(token);
        foreach (var item in token.Classes)
        {
            // Registry lookup proves publication before dereferencing a captured native class.
            Require(LookupCellClass(item) == item.Pointer,
                "A native entity class was removed or replaced while its cell remained owned.");
            ValidateCellClassIdentity(item);
            if (item.Script == 0) continue;
            ValidateCellClassScript(token, item);
            if (captureNewPrototype && item.Prototype == 0) CaptureCellClassPrototype(item);
            Require(Marshal.ReadIntPtr(item.Script + 0x10) == item.Prototype,
                "The retained native entity class prototype was reset or replaced.");
            if (item.Prototype != 0)
                Require(Marshal.ReadIntPtr(item.Prototype) == item.PrototypeTable &&
                    Marshal.ReadByte(item.Script + 0xd8) != 0,
                    "The retained native class prototype is incomplete or changed implementation.");
        }
    }

    private void CaptureCellClassPrototype(CellEntityClass item)
    {
        item.Prototype = Marshal.ReadIntPtr(item.Script + 0x10);
        // Native skip and normally returned script-load failure can both leave this null.
        // Class+38 is an attempted-load flag, not evidence that3202C5C0 succeeded.
        if (item.Prototype == 0) { item.PrototypeTable = 0; return; }
        Require(Marshal.ReadByte(item.Script + 0xd8) != 0,
            "The native class cache published an incomplete script prototype.");
        item.PrototypeTable = Marshal.ReadIntPtr(item.Prototype);
        Require(item.PrototypeTable != 0, "The native class prototype has no script-table implementation.");
    }

    private nint LookupCellClass(CellEntityClass item) =>
        Native(() => Bind<CellClassLookupCall>(_registry, 0x18, 0x95540)(_registry, item.NameInput));

    private void ValidateCellClassRegistry()
    {
        Require(Marshal.ReadIntPtr(_lease.EntitySystem + 0x401b0) == _registry,
            "The native entity class registry no longer belongs to this engine generation.");
        RequireTable(_registry, 0xd9498);
    }

    private void CaptureCellClassServices(CellEntityClasses token)
    {
        token.ScriptSystem = Marshal.ReadIntPtr(_lease.Environment + 0x40);
        token.Binding = Marshal.ReadIntPtr(_lease.EntitySystem + 0x401a0);
        if (token.ScriptSystem == 0 || token.Binding == 0)
            throw new NotSupportedException("Native cell scripts require the real script system and entity binding owner.");
        token.ScriptSystemTable = Marshal.ReadIntPtr(token.ScriptSystem);
        token.BindingTable = Marshal.ReadIntPtr(token.Binding);
        token.BindingPrototype = Marshal.ReadIntPtr(token.Binding + 0x48);
        Require(token.ScriptSystemTable != 0 && token.BindingTable != 0 && token.BindingPrototype != 0,
            "The native script system or entity prototype binding is incomplete.");
    }

    private void ValidateCellClassServices(CellEntityClasses token)
    {
        Require(Marshal.ReadIntPtr(_lease.Environment + 0x40) == token.ScriptSystem &&
            Marshal.ReadIntPtr(_lease.EntitySystem + 0x401a0) == token.Binding,
            "Native entity scripting services changed while cell classes remained retained.");
        Require(Marshal.ReadIntPtr(token.ScriptSystem) == token.ScriptSystemTable &&
            Marshal.ReadIntPtr(token.Binding) == token.BindingTable &&
            Marshal.ReadIntPtr(token.Binding + 0x48) == token.BindingPrototype,
            "The native entity script implementation or bound prototype changed.");
    }

    private void ValidateCellClassIdentity(CellEntityClass item)
    {
        RequireTable(item.Pointer, 0xd9298);
        Require(unchecked((uint)Marshal.ReadInt32(item.Pointer + 8)) == item.Flags &&
            ReadCellClassString(Marshal.ReadIntPtr(item.Pointer + 0x10)) == item.NativeName &&
            ReadCellClassString(Marshal.ReadIntPtr(item.Pointer + 0x18)) == item.ScriptPath &&
            Marshal.ReadIntPtr(item.Pointer + 0x20) == item.Script &&
            Marshal.ReadIntPtr(item.Pointer + 0x28) == 0 && Marshal.ReadIntPtr(item.Pointer + 0x30) == 0,
            "The captured native entity class changed its flags, script or creator ownership.");
    }

    private void ValidateCellClassScript(CellEntityClasses token, CellEntityClass item)
    {
        RequireTable(item.Script, 0xd07f0);
        Require(Marshal.ReadIntPtr(item.Script + 8) == token.ScriptSystem &&
            ReadCellClassString(Marshal.ReadIntPtr(item.Script + 0x20)) == item.Name &&
            ReadCellClassString(Marshal.ReadIntPtr(item.Script + 0x28)) == item.ScriptPath,
            "The class script belongs to another script system, name or path.");
        //320293A0/3202C5C0 need real game/model callbacks for nondefault discriminators.
        if (Marshal.ReadInt32(item.Script + 0x40) != Marshal.ReadInt32(_lease.EntityModule + 0xf6928) ||
            Marshal.ReadIntPtr(item.Script + 0x30) != 0 || Marshal.ReadIntPtr(item.Script + 0x38) != 0)
            throw new NotSupportedException($"Entity script '{item.Name}' requires native game/model or derived-class services.");
        // This identity check does not establish Boids, game, physics or other Lua API support.
        // Native Spawn/Init and its required proxy owners remain the batch's separate boundary.
    }

    private (nint Pointer, nint Table, int Value) ReadCellEntityQuality()
    {
        nint variable;
        if ((Marshal.ReadByte(_lease.EntityModule + 0xfd6f8) & 1) != 0)
            variable = Marshal.ReadIntPtr(_lease.EntityModule + 0xfd6f0);
        else
        {
            var console = Marshal.ReadIntPtr(_lease.Environment + 0x38);
            if (console == 0) throw new NotSupportedException("Native MinSpec admission requires the real console provider.");
            Require(Marshal.ReadIntPtr(console) != 0, "The native console provider has no implementation.");
            byte* storage = stackalloc byte[16];
            new Span<byte>(storage, 16).Clear();
            var name = (nint)storage;
            Marshal.WriteIntPtr(name + 8, _lease.EntityModule + 0xd9718); // Actual CName: e_obj_quality.
            variable = Native(() => Virtual<CellQualityLookupCall>(console, 0xc0)(console, name));
            // Leave FD6F0/FD6F8 to32097000; preparation never changes the native cache.
        }
        if (variable == 0) return (0, 0, 0);
        var table = Marshal.ReadIntPtr(variable);
        Require(table != 0, "The native quality variable has no implementation.");
        var value = Native(() => Virtual<CellPrerequisiteIntegerCall>(variable, 0x10)(variable));
        return (variable, table, value);
    }

    private void CaptureCellFlowGraphPrerequisite(CellEntityClasses token, ZoneCellEntityRow[] rows)
    {
        token.HasFlowGraph = rows.Any(row => row.FlowGraph is not null);
        if (!token.HasFlowGraph) return;
        (token.SystemMode, token.FlowGraphSuppressed, token.Server) = ReadCellFlowGraphGates();
        token.NeedsFlowSystem = !token.FlowGraphSuppressed && rows.Any(row =>
        {
            var graph = row.FlowGraph;
            if (graph is null || !ZoneCellEntityValues.Boolean(graph, "enabled", true)) return false;
            return graph.Attribute("MultiPlayer") switch
            {
                "ClientOnly" => token.SystemMode != 2,
                "ClientServer" => true,
                _ => token.Server
            };
        });
        if (!token.NeedsFlowSystem) return;
        token.FlowSystem = Native(() => BindCellSystem<PointerCall>(_lease.System, 0x268, 0xb5f0)(_lease.System));
        if (token.FlowSystem == 0)
            throw new NotSupportedException("An eligible enabled cell FlowGraph requires CSystem's real FlowSystem provider; no replacement graph is created.");
        token.FlowSystemTable = Marshal.ReadIntPtr(token.FlowSystem);
        Require(token.FlowSystemTable != 0, "The native FlowSystem has no implementation.");
        token.FlowGraphFactory = Marshal.ReadIntPtr(token.FlowSystemTable + 0x20);
        if (token.FlowGraphFactory == 0)
            throw new NotSupportedException("The native FlowSystem has no actual graph factory.");
    }

    private (int Mode, bool Suppressed, bool Server) ReadCellFlowGraphGates()
    {
        //32091770 reads these real gates before constructing a graph. The mode1 callback is
        // conditional in native code, too; do not enter game/editor services in other modes.
        var mode = Native(() => BindCellSystem<CellPrerequisiteIntegerCall>(_lease.System, 0x178, 0xb360)(_lease.System));
        var suppressed = mode == 1 &&
            Native(() => BindCellSystem<CellPrerequisiteByteCall>(_lease.System, 0x1a0, 0xb9f40)(_lease.System)) != 0;
        return (mode, suppressed, Marshal.ReadByte(_lease.Environment + 0x121) != 0);
    }

    private void ValidateCellClassAdmission(CellEntityClasses token)
    {
        if (token.HasMinimumSpec)
        {
            var quality = ReadCellEntityQuality();
            Require(quality.Pointer == token.QualityVariable && quality.Table == token.QualityTable && quality.Value == token.CurrentSpec,
                "Native MinSpec admission changed during cell loading.");
        }
        if (!token.HasFlowGraph) return;
        var gates = ReadCellFlowGraphGates();
        Require(gates.Mode == token.SystemMode && gates.Suppressed == token.FlowGraphSuppressed && gates.Server == token.Server,
            "Native FlowGraph admission changed during cell loading.");
        if (!token.NeedsFlowSystem) return;
        Require(Native(() => BindCellSystem<PointerCall>(_lease.System, 0x268, 0xb5f0)(_lease.System)) == token.FlowSystem,
            "The cell's actual FlowSystem provider changed.");
        Require(Marshal.ReadIntPtr(token.FlowSystem) == token.FlowSystemTable &&
            Marshal.ReadIntPtr(token.FlowSystemTable + 0x20) == token.FlowGraphFactory,
            "The retained FlowSystem implementation or graph factory changed.");
    }

    private void RequireCellClassToken(CellEntityClasses token, bool prepared)
    {
        Require(ReferenceEquals(token.Owner, this) && _cellEntityClasses.Contains(token) && !token.Retired &&
            (!prepared || token.Prepared), "The cell no longer owns its exact native class admission token.");
    }

    // Called only after the batch's entities/proxies/deferred removals are retired. Partial
    // preparation remains caller-owned on ordinary failure. Native classes/tables stay with
    // CSystem; releasing this borrowing token never unregisters classes or unloads their Lua.
    private void ReleaseCellEntityClasses(CellEntityClasses token)
    {
        _lease.RequireActiveOperation();
        RequireCellClassToken(token, prepared: false);
        Require(_cellClassLoadScope is null, "Finish native class callbacks before releasing their borrowing token.");
        foreach (var item in token.Classes)
        {
            Marshal.FreeHGlobal(item.NameInput);
            item.NameInput = 0;
        }
        token.Retired = true;
        _cellEntityClasses.Remove(token);
    }

    // Root calls this before EntityLease.Release, after cell-batch retirement.
    private void RequireCellEntityClassesReleased()
    {
        if (_cellEntityClasses.Count != 0 || _cellClassLoadScope is not null)
            throw new InvalidOperationException("Retire every cell entity batch before releasing native class/script ownership.");
    }

    private string ReadCellClassString(nint pointer)
    {
        Require(pointer != 0, "A native entity class string is missing.");
        var length = Marshal.ReadInt32(pointer - 8);
        Require(length >= 0 && length <= 4096 && Marshal.ReadByte(pointer + length) == 0,
            "A native entity class string exceeds the managed bound or is unterminated.");
        return Marshal.PtrToStringUTF8(pointer, length)!;
    }

    internal sealed class CellEntityClasses(ZoneNativeEntityServices owner)
    {
        internal readonly ZoneNativeEntityServices Owner = owner;
        internal readonly List<CellEntityClass> Classes = [];
        internal nint ScriptSystem, ScriptSystemTable, Binding, BindingTable, BindingPrototype;
        internal nint QualityVariable, QualityTable, FlowSystem, FlowSystemTable, FlowGraphFactory;
        internal int CurrentSpec, SystemMode;
        internal bool HasMinimumSpec, HasFlowGraph, FlowGraphSuppressed, Server, NeedsFlowSystem, Prepared, Retired;
        internal int Count => Classes.Count;
    }

    internal sealed class CellEntityClass(string name)
    {
        internal readonly string Name = name;
        internal string NativeName = "", ScriptPath = "";
        internal uint Flags;
        internal nint NameInput, Pointer, Script, Prototype, PrototypeTable;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CellClassLookupCall(nint registry, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CellQualityLookupCall(nint console, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CellPrerequisiteIntegerCall(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte CellPrerequisiteByteCall(nint instance);
}
