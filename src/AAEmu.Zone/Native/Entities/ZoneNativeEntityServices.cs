using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Native.Entities;

/// <summary>
/// Owns native XML cell batches and plain Default-class entities with their real slot-zero character
/// resources in one CSystem generation. GameObject/ActorSystem and player admission remain separate.
/// All creation and retirement runs on the engine's owner thread under its shared operation gate.
/// </summary>
public sealed unsafe partial class ZoneNativeEntityServices : IDisposable
{
    private readonly ZoneNativeEngine.EntityLease _lease;
    private readonly nint _registry;
    private readonly nint _defaultClass;
    private readonly SpawnCall _spawn;
    private readonly InitializeCall _initialize;
    private readonly LookupCall _lookup;
    private readonly RemoveCall _remove;
    private readonly List<Entity> _entities = [];
    private bool _disposed;
    private bool _poisoned;

    internal ZoneNativeEntityServices(ZoneNativeEngine.EntityLease lease)
    {
        _lease = lease;
        // Read-only construction: the engine roots this adapter before any native allocation.
        Require(Marshal.ReadIntPtr(lease.EntityModule + 0xf4220) == lease.Environment &&
            Marshal.ReadIntPtr(lease.EntityModule + 0xf4228) == lease.EntitySystem,
            "CryEntitySystem does not belong to the captured CSystem environment.");
        RequireTable(lease.EntitySystem, 0xcfd48);
        _registry = Marshal.ReadIntPtr(lease.EntitySystem + 0x401b0); // IEntitySystem +50, 3201BD60.
        RequireTable(_registry, 0xd9498);
        _defaultClass = Marshal.ReadIntPtr(_registry + 0x28); // Registry +20, 32094D60.
        ValidateDefaultClass();
        _spawn = Bind<SpawnCall>(lease.EntitySystem, 0x58, 0x1da90);
        _initialize = Bind<InitializeCall>(lease.EntitySystem, 0x60, 0x1fd30);
        _lookup = Bind<LookupCall>(lease.EntitySystem, 0x70, 0x1c270);
        _remove = Bind<RemoveCall>(lease.EntitySystem, 0x90, 0x1f330);
    }

    /// <summary>
    /// Allocates a native entity ID, loads the authored ModelFile synchronously into slot zero,
    /// and then initializes its native proxies. Loading uses IEntity+338(0,path,2), including the
    /// real character-provider callbacks and ownership; no dummy character or X2 ModelDesc is made.
    /// Dispose the returned entity after its physical body, and dispose this service before CSystem.
    /// </summary>
    public Entity CreateInitialCharacter(ZoneActorModel model, string name, Vector3 position,
        Quaternion orientation, float scale)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(model);
        ValidateString(name, nameof(name));
        ValidateString(model.Definition.ModelFile, nameof(model));
        if (!Finite(position) || !Finite(new(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || !float.IsFinite(orientation.LengthSquared()) ||
            orientation.LengthSquared() <= 0 || !float.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("Entity pose must be finite, with a nonzero rotation and positive scale.");
        ValidateDefaultClass();
        var entity = new Entity(this, model, name);
        _entities.Add(entity); // Root all partial ownership before calling native code.
        try
        {
            entity.AllocateInputs(position, orientation, scale);
            // 3201DA90 registers before Init. Loading before 3201FD30 reproduces 3201FE80's
            // required slot-before-proxy-init order without passing an invented native ModelDesc.
            // The step markers name WHICH native call reaches the fatal IGame+0xE8 character-spawn
            // callback: the Agent 6 login proved the callback is reached from this sequence, and
            // the marker is what turns that into a call-site claim.
            entity.StepMarker = "spawn";
            _stepMarker = entity.StepMarker;
            RecordStep(entity.StepMarker);
            entity.Pointer = Native(() => _spawn(_lease.EntitySystem, entity.Parameters, 0, 0));
            Require(entity.Pointer != 0,
                "Native entity allocation returned null; allocator ownership cannot be established.");
            RequireTable(entity.Pointer, 0xcd998);
            entity.IdValue = unchecked((uint)Marshal.ReadInt32(entity.Pointer + 0x14));
            Require(entity.IdValue != 0, "Native entity allocation returned an invalid identity.");
            ValidateEntity(entity);
            var load = Bind<LoadCharacterCall>(entity.Pointer, 0x338, 0xf140);
            entity.StepMarker = "load-character";
            _stepMarker = entity.StepMarker;
            // The real loader address is the entity vtable's own slot (0xf140 above is only the
            // null-slot sentinel). Logging the resolved entry turns the fatal callback into a
            // decompilable call site instead of an address nobody can follow.
            RecordStep($"load-character-entry=0x{Marshal.ReadIntPtr(Marshal.ReadIntPtr(entity.Pointer) + 0x338):X}");
            if (Native(() => load(entity.Pointer, 0, entity.ModelPath, 2)) != 0)
                throw new InvalidOperationException($"Native character slot zero failed to load '{model.Definition.ModelFile}'.");
            CaptureCharacter(entity);
            entity.StepMarker = "initialize";
            _stepMarker = entity.StepMarker;
            RecordStep(entity.StepMarker);
            if (Native(() => _initialize(_lease.EntitySystem, entity.Pointer, entity.Parameters)) == 0)
                throw new InvalidOperationException("Native entity initialization rejected the character entity.");
            ValidateEntity(entity);
            Require((Marshal.ReadInt32(entity.Pointer + 0x10) & 8) != 0,
                "Native entity initialization did not complete.");
            ValidateCharacter(entity);
            return entity;
        }
        catch (Exception error)
        {
            // An uncertain native call poisons the owning process; retain its allocations and
            // module roots. A normal returned failure can be retired through the exact native ID.
            if (_poisoned) throw;
            try { Retire(entity); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        using var operation = Enter();
        if (_entities.Any(entity => entity.PhysicsLease is not null))
            throw new InvalidOperationException("Dispose physical bodies before their native entity service.");
        RetireCellEntityBatches();
        foreach (var entity in _entities.ToArray()) Retire(entity);
        RequireCellEntityClassesReleased();
        _lease.Release();
        _disposed = true;
    }

    private IDisposable Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _lease.Enter(); // Stale managed owners are rejected before native memory reads.
    }

    private void ValidateDefaultClass()
    {
        Require(Marshal.ReadIntPtr(_lease.EntitySystem + 0x401b0) == _registry &&
            Marshal.ReadIntPtr(_registry + 0x28) == _defaultClass,
            "The native default-class registry generation changed.");
        RequireTable(_defaultClass, 0xd9298);
        // 32094D70 registers literal Default, flags3, empty script and null creator/user data.
        var name = Marshal.ReadIntPtr(_defaultClass + 0x10);
        var script = Marshal.ReadIntPtr(_defaultClass + 0x18);
        Require(name != 0 && Marshal.PtrToStringUTF8(name) == "Default" &&
            Marshal.ReadInt32(_defaultClass + 8) == 3 && script != 0 && Marshal.ReadByte(script) == 0 &&
            Marshal.ReadIntPtr(_defaultClass + 0x20) == 0 &&
            Marshal.ReadIntPtr(_defaultClass + 0x28) == 0 && Marshal.ReadIntPtr(_defaultClass + 0x30) == 0,
            "The actual engine Default class is unavailable or has an unowned script/creator.");
    }

    private void ValidateEntity(Entity entity)
    {
        Require(entity.Pointer != 0 &&
            Native(() => _lookup(_lease.EntitySystem, entity.IdValue)) == entity.Pointer,
            "The exact native entity registration is no longer owned by this adapter.");
        RequireTable(entity.Pointer, 0xcd998);
        Require(Marshal.ReadIntPtr(entity.Pointer + 0x30) == _defaultClass &&
            unchecked((uint)Marshal.ReadInt32(entity.Pointer + 0x14)) == entity.IdValue &&
            (Marshal.ReadInt32(entity.Pointer + 0x10) & 0x20) == 0,
            "The owned native entity has been replaced or retired.");
    }

    private void CaptureCharacter(Entity entity)
    {
        ValidateEntity(entity);
        var getCharacter = Bind<SlotCall>(entity.Pointer, 0x2e0, 0xeaa0);
        entity.Character = Native(() => getCharacter(entity.Pointer, 0));
        if (entity.Character == 0)
            throw new InvalidOperationException("Native slot loading returned no real character instance.");
        entity.CharacterTable = Marshal.ReadIntPtr(entity.Character);
        entity.Skeleton = Native(() => Virtual<PointerCall>(entity.Character, 0x30)(entity.Character));
        if (entity.Skeleton == 0)
            throw new InvalidOperationException("The native character has no skeleton pose.");
        entity.SkeletonTable = Marshal.ReadIntPtr(entity.Skeleton);
    }

    private void ValidateCharacter(Entity entity)
    {
        ValidateEntity(entity);
        Require(Native(() => Bind<SlotCall>(entity.Pointer, 0x2e0, 0xeaa0)(entity.Pointer, 0)) == entity.Character &&
            entity.Character != 0 && Marshal.ReadIntPtr(entity.Character) == entity.CharacterTable,
            "The native character slot was replaced outside its owner.");
        Require(Native(() => Virtual<PointerCall>(entity.Character, 0x30)(entity.Character)) == entity.Skeleton &&
            entity.Skeleton != 0 && Marshal.ReadIntPtr(entity.Skeleton) == entity.SkeletonTable,
            "The native skeleton pose was replaced outside its owner.");
    }

    private void RequirePlainPhysicsOwner(Entity entity)
    {
        ValidateCharacter(entity);
        Require(Native(() => Bind<PointerCall>(entity.Pointer, 0x218, 0xe450)(entity.Pointer)) == 0 &&
            Native(() => Virtual<PointerCall>(entity.Skeleton, 0x138)(entity.Skeleton)) == 0,
            "A native entity proxy or skeleton already owns physicalization; a separate plain body is not admissible.");
    }

    private void Retire(Entity entity)
    {
        if (entity.Retired) return;
        if (entity.PhysicsLease is not null)
            throw new InvalidOperationException("Dispose the physical body before its native entity.");
        if (entity.Pointer != 0)
        {
            // Failed Init may already have run 3201F170 and freed the entity. Lookup first;
            // never inspect the captured pointer until full native ID + pointer identity matches.
            var current = Native(() => _lookup(_lease.EntitySystem, entity.IdValue));
            if (current != 0)
            {
                Require(current == entity.Pointer, "Entity cleanup encountered a replacement registration.");
                RequireTable(current, 0xcd998);
                Require(Marshal.ReadInt32(current + 0xd8) == 0,
                    "Native entity cleanup is still retained by another keep-alive owner.");
                Native(() => { _remove(_lease.EntitySystem, entity.IdValue, 1); return 0; });
                Require(Native(() => _lookup(_lease.EntitySystem, entity.IdValue)) == 0,
                    "Native entity removal was vetoed or deferred; its resource lifetime remains retained.");
            }
            // Native shutdown clears slot animation/skeleton callbacks before provider Release.
            entity.Pointer = 0;
            entity.Character = 0;
            entity.Skeleton = 0;
        }
        entity.FreeInputs();
        entity.Retired = true;
        _entities.Remove(entity);
    }

    private T Native<T>(Func<T> call)
    {
        try
        {
            var result = call();
            _lease.ThrowIfFaulted(); // A native callback can capture a fault without unwinding.
            return result;
        }
        catch (Exception error)
        {
            _poisoned = true; _lease.Poison(error);
            // Name the exact step that entered native code before the fault: the spawn/load/init
            // sequence is what reaches the fatal IGame+0xE8 character-spawn callback, and the
            // marker turns "admission failed" into a call-site claim.
            if (_stepMarker.Length != 0)
                Console.WriteLine($"Entity native step '{_stepMarker}' faulted: {error.GetType().Name}: {error.Message}");
            throw;
        }
    }

    private string _stepMarker = string.Empty;

    /// <summary>
    /// Optional evidence file that receives one flushed line BEFORE each native spawn step
    /// (spawn / load-character / initialize). The steps are written ahead of the call because the
    /// IGame+0xE8 placeholder FailFasts the process outright, which leaves no exception to log:
    /// the last line in this file is the last step that was actually entered.
    /// </summary>
    public static string? StepEvidencePath { get; set; }

    private static void RecordStep(string step)
    {
        var path = StepEvidencePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.AppendAllText(path, $"step={step} utc={DateTime.UtcNow:O}{Environment.NewLine}");
        }
        catch
        {
            // Evidence is best effort: never let the recorder change admission behavior.
        }
    }

    private void Require(bool condition, string message)
    {
        if (condition) return;
        var error = new InvalidOperationException(message);
        _poisoned = true;
        _lease.Poison(error);
        throw error;
    }

    private void RequireTable(nint pointer, int relativeAddress) =>
        Require(pointer != 0 && Marshal.ReadIntPtr(pointer) == _lease.EntityModule + relativeAddress,
            "The native EntitySystem object has an unexpected implementation identity.");

    private T Bind<T>(nint pointer, int slot, int relativeAddress) where T : Delegate
    {
        var target = Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer) + slot);
        Require(target == _lease.EntityModule + relativeAddress,
            "The native entity operation no longer matches the admitted module ABI.");
        return Marshal.GetDelegateForFunctionPointer<T>(target);
    }

    private static T Virtual<T>(nint pointer, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer) + slot));

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void ValidateString(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new ArgumentException("Native names and model paths must be nonempty strings without embedded NUL.", parameter);
    }

    /// <summary>
    /// An actual native entity and its slot-zero character. Metadata is copied; raw native access
    /// is available only as the engine-validated, independently retained physics capability.
    /// </summary>
    public sealed partial class Entity : IDisposable, IZoneNativePhysicsForeignData
    {
        internal readonly ZoneNativeEntityServices _owner;
        internal nint Pointer, Character, CharacterTable, Skeleton, SkeletonTable;
        internal nint Parameters, ModelPath, NativeName, EmptyString;
        internal uint IdValue;
        internal bool Retired;
        /// <summary>Which spawn/load/init step is currently entering native code (fault evidence).</summary>
        internal string StepMarker = string.Empty;
        internal ForeignLease? PhysicsLease;
        public uint Id => IdValue;
        public ZoneActorModel Model { get; }
        public string Name { get; }

        internal Entity(ZoneNativeEntityServices owner, ZoneActorModel model, string name)
        { _owner = owner; Model = model; Name = name; }

        internal void AllocateInputs(Vector3 position, Quaternion orientation, float scale)
        {
            ModelPath = Marshal.StringToCoTaskMemUTF8(Model.Definition.ModelFile);
            NativeName = Marshal.StringToCoTaskMemUTF8(Name);
            EmptyString = Marshal.StringToCoTaskMemUTF8("");
            Parameters = Marshal.AllocHGlobal(0x98);
            new Span<byte>((void*)Parameters, 0x98).Clear();
            // Original 39277D00 producer, consumed by 32013B30. Automatic native ID/GUID,
            // no archetype/creator/ModelDesc or properties. 32016930 proves quaternion XYZW.
            *(SpawnParameters*)Parameters = new SpawnParameters
            {
                Class = _owner._defaultClass, LayerName = EmptyString, Name = NativeName,
                Flags = Marshal.ReadByte(_owner._lease.Environment + 0x121) == 0 ? 0x100u : 0x200u,
                Position = position, Orientation = orientation, Scale = new Vector3(scale),
                ModelScale = scale, CellX = -1, CellY = -1
            };
        }

        internal void FreeInputs()
        {
            Marshal.FreeHGlobal(Parameters); Parameters = 0;
            Marshal.FreeCoTaskMem(ModelPath); ModelPath = 0;
            Marshal.FreeCoTaskMem(NativeName); NativeName = 0;
            Marshal.FreeCoTaskMem(EmptyString); EmptyString = 0;
        }

        public void Dispose()
        {
            if (Retired) return;
            using var operation = _owner.Enter();
            _owner.Retire(this);
        }

        public IZoneNativePhysicsForeignDataLease AcquirePhysicsLease()
        {
            // The physics adapter already owns the common gate. Public callers cannot acquire
            // a raw IEntity capability outside that exact engine operation.
            _owner._lease.RequireActiveOperation();
            ObjectDisposedException.ThrowIf(Retired, this);
            if (PhysicsLease is not null)
                throw new InvalidOperationException("This entity already has a physical-body association.");
            _owner.RequirePlainPhysicsOwner(this);
            var count = Marshal.ReadInt32(Pointer + 0xd8);
            _owner.Require(count == 0, "The plain entity has another native keep-alive owner.");
            var retained = new ForeignLease(this, Pointer);
            PhysicsLease = retained; // Root association before the actual native retention.
            _owner.Native(() => { _owner.Bind<VoidCall>(Pointer, 0x3b8, 0x16d30)(Pointer); return 0; });
            _owner.Require(Marshal.ReadInt32(Pointer + 0xd8) == 1,
                "Native IEntity keep-alive acquisition did not complete exactly once.");
            return retained;
        }
    }

    internal sealed class ForeignLease : IZoneNativeEnginePhysicsForeignDataLease
    {
        // The native foreign-data kind of this codebase's engine IEntity lease. Kind 1 marks the
        // natively created static-object bodies, which no CellEntityBatch ever owns.
        internal const short LeaseKind = 2;
        private readonly Entity _entity;
        private bool _released;
        public nint Pointer { get; private set; }
        public short Kind => LeaseKind;
        public uint EntityId => _entity.IdValue;
        public ZoneNativeEngine Engine => _entity._owner._lease.Engine;
        internal ForeignLease(Entity entity, nint pointer) { _entity = entity; Pointer = pointer; }
        public void Dispose()
        {
            if (_released) return;
            var owner = _entity._owner;
            owner._lease.RequireActiveOperation();
            owner.Require(ReferenceEquals(_entity.PhysicsLease, this) && _entity.Pointer == Pointer,
                "The physical body no longer owns this exact native entity lease.");
            owner.RequirePlainPhysicsOwner(_entity);
            owner.Require(Marshal.ReadInt32(Pointer + 0xd8) == 1,
                "The native IEntity keep-alive ownership changed before release.");
            owner.Native(() => { owner.Bind<VoidCall>(Pointer, 0x3c0, 0x16d40)(Pointer); return 0; });
            owner.Require(Marshal.ReadInt32(Pointer + 0xd8) == 0,
                "Native IEntity keep-alive release did not complete exactly once.");
            _entity.PhysicsLease = null;
            Pointer = 0;
            _released = true;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x98)]
    private struct SpawnParameters
    {
        [FieldOffset(0x10)] public nint Class;
        [FieldOffset(0x20)] public nint LayerName;
        [FieldOffset(0x28)] public nint Name;
        [FieldOffset(0x30)] public uint Flags;
        [FieldOffset(0x38)] public Vector3 Position;
        [FieldOffset(0x44)] public Quaternion Orientation;
        [FieldOffset(0x54)] public Vector3 Scale;
        [FieldOffset(0x70)] public float ModelScale;
        [FieldOffset(0x90)] public int CellX;
        [FieldOffset(0x94)] public int CellY;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SpawnCall(nint system, nint parameters, byte initialize, byte special);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte InitializeCall(nint system, nint entity, nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LookupCall(nint system, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RemoveCall(nint system, uint id, byte immediate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadCharacterCall(nint entity, int slot, nint path, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SlotCall(nint entity, int slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint PointerCall(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall(nint instance);
}
