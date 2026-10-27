using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private VegetationGroups? _vegetationGroups;
    private readonly List<Vegetation> _vegetation = [];

    /// <summary>
    /// Loads the authored world's vegetation.xml through the real CSystem XML service and
    /// 30109F90's descriptor/map/material/publication helpers. Group IDs come from XML, including
    /// native later-row replacement. No IWorld, geometry, or missing numeric defaults are synthesized.
    /// The engine dispatcher must remain in its idle pre-update phase throughout this owner.
    /// </summary>
    public VegetationGroups? LoadVegetationGroups(ZoneWorldMapContent world)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(world);
        if (_vegetationGroups is not null)
            throw new InvalidOperationException("Retire the existing vegetation group owner before loading another world.");
        RequireIdleVegetationWorld();
        var manager = Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa968);
        if (manager == 0)
            throw new NotSupportedException("The initialized engine has no real vegetation manager.");
        RequireTable(manager, 0x28a7c8);
        if (Marshal.ReadInt64(manager + 0x18) != 0 || GroupVectorCount() != 0)
            throw new InvalidOperationException("This engine already has vegetation groups belonging to another owner.");
        var path = ZoneAssetPath.Validate($"game/worlds/{world.Header.Name}/vegetation.xml");
        var bytes = PathBytes(path);
        var groups = new VegetationGroups(this, world, path, manager);
        _vegetationGroups = groups; // Root every partially acquired native object before callbacks.
        try
        {
            Native(() => Bind<VoidCall>(manager, 8, 0x109650)(manager)); // Reset precedes file open.
            groups.PathPointer = Allocate(bytes);
            groups.XmlHandles = Marshal.AllocHGlobal(24);
            new Span<byte>((void*)groups.XmlHandles, 24).Clear();
            Native(() => Virtual<LoadVegetationXmlCall>(_lease.System, 0x378)(
                _lease.System, groups.XmlHandles, groups.PathPointer, 0));
            var root = Marshal.ReadIntPtr(groups.XmlHandles);
            if (root == 0)
            {
                Retire(groups, "LoadVegetationGroups:no-xml-root");
                return null;
            }
            WithAscii("groupList", name => Native(() =>
                Virtual<FindVegetationXmlCall>(root, 0x130)(root, groups.XmlHandles + 8, name)));
            var list = Marshal.ReadIntPtr(groups.XmlHandles + 8);
            groups.Descriptor = Marshal.AllocHGlobal(0xb0);
            var children = list == 0 ? 0 : Native(() => Virtual<ReferenceCall>(list, 0x120)(list));
            Require(children >= 0, "The real vegetation XML service returned an invalid child count.");
            for (var index = 0; index < children; index++)
            {
                Native(() => Virtual<VegetationXmlChildCall>(list, 0x128)(
                    list, groups.XmlHandles + 16, index));
                var child = Marshal.ReadIntPtr(groups.XmlHandles + 16);
                Require(child != 0, "The real vegetation XML service returned a missing group child.");
                InitializeVegetationDescriptor(groups);
                ValidateVegetationNumericAttributes(child);
                Native(() => Terrain<ParseVegetationGroupCall>(0x107f70)(
                    manager, groups.XmlHandles + 16, groups.Descriptor, 1));
                var entry = Native(() => Terrain<VegetationMapEntryCall>(0x10e580)(
                    manager + 8, groups.Descriptor));
                Native(() => Terrain<CopyVegetationGroupCall>(0x10d300)(entry, groups.Descriptor));
                DestroyVegetationDescriptor(groups);
                ReleaseVegetationXml(groups.XmlHandles + 16);
                groups.AuthoredRowCount++;
            }

            var records = EnumerateVegetationRecords(groups);
            groups.DefinitionCount = records.Count;
            Marshal.WriteInt32(manager + 0x28, unchecked(records.Count * 2));
            foreach (var unused in records) PumpVegetationLoad(groups);
            foreach (var record in records)
            {
                var name = Marshal.ReadIntPtr(record + 0x98);
                var material = Marshal.ReadInt32(name - 8) == 0 ? 0 :
                    Native(() => _loadMaterial(_materials, name, 1, 0, 0));
                // 30109F90 stores a borrowed material here; 30108AD0/SetStatInstGroup retain it.
                Marshal.WriteIntPtr(record + 0x10, material);
                PumpVegetationLoad(groups);
            }
            foreach (var record in records)
                Native(() => Terrain<PublishVegetationGroupCall>(0x108ad0)(manager, record));
            groups.StreamDummyGroupId = CreateVegetationDummy(groups, "objects/box_nodraw.cgf", 0x30);
            groups.DefaultDummyGroupId = CreateVegetationDummy(groups, "objects/default.cgf", 0x34);
            groups.VectorBegin = Marshal.ReadIntPtr(_objects + 0x58);
            groups.VectorCount = GroupVectorCount();
            groups.ReferenceCounts = new int[groups.VectorCount];
            for (var index = 0; index < groups.VectorCount; index++)
                groups.ReferenceCounts[index] = Marshal.ReadInt32(groups.VectorBegin + index * 0x510 + 0x500);
            groups.Loaded = true;
            ReleaseVegetationLoadInputs(groups);
            return groups;
        }
        catch (Exception error)
        {
            if (_poisoned) throw;
            try { Retire(groups, "LoadVegetationGroups:rollback"); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Calls native _LoadVegetation302221D0 on the exact 64-byte payload shared by tagged
    /// type-two objects and vegetation.dat sector rows. The caller supplies the native cell or
    /// sector translation. Native configuration, scale, model and on-demand physics gates remain
    /// effective. A null result or a node without a physical entity does not establish collision readiness.
    /// </summary>
    public Vegetation? CreateVegetation(VegetationGroups groups, Vector3 translation, ReadOnlySpan<byte> rawPayload)
    {
        using var operation = Enter();
        ValidateVegetationGroups(groups, true);
        _lease.RequireTerrainEntityGrid();
        if (rawPayload.Length != 64)
            throw new ArgumentException("A native vegetation payload must contain exactly 64 bytes.", nameof(rawPayload));
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z))
            throw new ArgumentException("The native vegetation translation must be finite.", nameof(translation));
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        RequireIdleVegetationWorld();
        var value = new Vegetation(this, groups);
        _vegetation.Add(value);
        try
        {
            value.RecordPointer = Allocate(rawPayload.ToArray());
            value.TranslationPointer = Marshal.AllocHGlobal(12);
            *(Vector3*)value.TranslationPointer = translation;
            value.CursorPointer = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.CursorPointer, value.RecordPointer);
            value.GroupId = Marshal.ReadInt16(value.RecordPointer + 0x37);
            value.Pointer = Native(() => Terrain<CreateVegetationCall>(0x2221d0)(
                value.TranslationPointer, value.CursorPointer, 0));
            Require(Marshal.ReadIntPtr(value.CursorPointer) == value.RecordPointer + 64,
                "The native vegetation factory did not consume exactly one serialized row.");
            if (value.Pointer == 0)
            {
                Retire(value);
                return null;
            }
            ValidateVegetation(value);
            value.PhysicalPointer = Marshal.ReadIntPtr(value.Pointer + 0x80);
            var group = groups.VectorBegin + value.GroupId * 0x510;
            value.ModelPointer = Marshal.ReadIntPtr(group + 8);
            RequireTable(value.ModelPointer, 0x27ee78);
            value.ModelWasFallback = value.ModelPointer == Marshal.ReadIntPtr(_objects + 0x168);
            value.NativePosition = *(Vector3*)(value.Pointer + 0x58);
            value.NativeScale = *(float*)(value.Pointer + 0x68);
            if (value.PhysicalPointer != 0)
            {
                ValidateVegetationBody(value);
                value.PhysicalSimulationClass = Marshal.ReadByte(value.PhysicalPointer + 0x47);
            }
            return value;
        }
        catch (Exception error)
        {
            if (_poisoned) throw;
            try { Retire(value); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>Completes the serialized-cell metadata used by both 30225050 and 302253C0.</summary>
    public void CompleteCellVegetation(Vegetation value)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Owner, this) || value.Retired)
            throw new InvalidOperationException("Cell publication requires live vegetation from this service.");
        if (value.IsCellPublicationComplete)
            throw new InvalidOperationException("This vegetation already completed cell publication metadata.");
        ValidateVegetation(value);
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        var state = Native(() => _cellPublicationByte!(_cellPublicationService));
        ValidateCellPublicationService();
        ValidateVegetation(value);
        Native(() =>
        {
            Marshal.WriteByte(value.Pointer + 0x39, (byte)(Marshal.ReadByte(value.Pointer + 0x39) | 1));
            Marshal.WriteByte(value.Pointer + 0x38, state);
        });
        value.NativeCellPublicationByte = state;
        value.IsCellPublicationComplete = true;
    }

    /// <summary>
    /// 302253C0 calls the real CSystem loading callback once after a nonempty valid sector,
    /// even when all its instance factories return null. The caller skips empty sectors.
    /// </summary>
    public void CompleteVegetationSector(VegetationGroups groups)
    {
        using var operation = Enter();
        ValidateVegetationGroups(groups, true);
        Native(() => Virtual<VoidCall>(_lease.System, 0x560)(_lease.System));
        ValidateVegetationGroups(groups, true);
    }

    private void InitializeVegetationDescriptor(VegetationGroups groups)
    {
        // 30109F90's inlined descriptor initialization. The native reader leaves absent numeric
        // attributes unspecified; the guard below admits only fields its real getters can parse.
        new Span<byte>((void*)groups.Descriptor, 0xb0).Clear();
        foreach (var offset in new[] { 0x20, 0x28, 0x30, 0x98, 0xa8 })
            Marshal.WriteIntPtr(groups.Descriptor + offset, _lease.TerrainModule + 0x2d4074);
        Marshal.WriteInt32(groups.Descriptor + 0x88, -1);
        Marshal.WriteInt32(groups.Descriptor + 0x8c, unchecked((int)0xbf800000));
        groups.DescriptorInitialized = true;
    }

    private void ValidateVegetationNumericAttributes(nint node)
    {
        // Preserve real XML conversion/NaN behavior. Reject native-uninitialized malformed input
        // rather than assigning guessed defaults; the configured world's 4,145 rows have all fields.
        var scratch = stackalloc byte[8];
        var output = (nint)scratch;
        foreach (var (name, slot) in VegetationNumericAttributes)
            WithAscii(name, attribute =>
            {
                if (Native(() => Virtual<VegetationAttributeCall>(node, slot)(node, attribute, output)) == 0)
                    throw new NotSupportedException($"Vegetation attribute '{name}' is absent or cannot be parsed by the native XML reader.");
            });
    }

    private static readonly (string Name, int Slot)[] VegetationNumericAttributes =
    [
        ("id", 0xe0), ("bHideability", 0x80), ("bHideabilitySecondary", 0x80), ("bPickable", 0x80),
        ("fBending", 0xc0), ("bComplexBending", 0x80), ("bCastShadow", 0x80),
        ("bCastShadowExtraRange", 0x80), ("bCastShadowTerrain", 0x80), ("bRecvShadow", 0x80),
        ("bPrecShadow", 0x80), ("bUseAlphaBlending", 0x80), ("fSpriteDistRatio", 0xc0),
        ("fShadowDistRatio", 0xc0), ("fMaxViewDistRatio", 0xc0), ("fBrightness", 0xc0),
        ("bUseSprites", 0x80), ("bSpriteCastShadow", 0x80), ("bRandomRotation", 0x80),
        ("bAlignToTerrain", 0x80), ("bUseTerrainColor", 0x80), ("bAffectedByVoxels", 0x80),
        ("fDensity", 0xc0), ("fElevationMax", 0xc0), ("fElevationMin", 0xc0),
        ("fSize", 0xc0), ("fSizeVar", 0xc0), ("fSlopeMax", 0xc0), ("fSlopeMin", 0xc0),
        ("minConfigSpec", 0xe0), ("nMaterialLayers", 0xd8), ("minDistSameObject", 0xc0),
        ("minDistOtherObject", 0xc0), ("doodadType", 0xe0), ("aiRadius", 0xc0)
    ];

    private List<nint> EnumerateVegetationRecords(VegetationGroups groups)
    {
        var sentinel = Marshal.ReadIntPtr(groups.Manager + 0x10);
        var count = Marshal.ReadInt64(groups.Manager + 0x18);
        Require(sentinel != 0 && count >= 0 && count <= int.MaxValue,
            "The vegetation manager has an invalid native map.");
        var records = new List<nint>((int)count);
        var node = Marshal.ReadIntPtr(sentinel);
        var visited = new HashSet<nint>();
        while (node != sentinel)
        {
            Require(node != 0 && Marshal.ReadByte(node + 0xd1) == 0 && visited.Add(node) && records.Count < count,
                "The vegetation manager's native map traversal is inconsistent.");
            records.Add(node + 0x20);
            var right = Marshal.ReadIntPtr(node + 0x10);
            if (Marshal.ReadByte(right + 0xd1) == 0)
            {
                node = right;
                while (Marshal.ReadByte(Marshal.ReadIntPtr(node) + 0xd1) == 0)
                    node = Marshal.ReadIntPtr(node);
            }
            else
            {
                var parent = Marshal.ReadIntPtr(node + 8);
                while (Marshal.ReadByte(parent + 0xd1) == 0 && node == Marshal.ReadIntPtr(parent + 0x10))
                {
                    node = parent;
                    parent = Marshal.ReadIntPtr(parent + 8);
                }
                node = parent;
            }
        }
        Require(records.Count == count, "The vegetation manager's map count changed during publication.");
        return records;
    }

    private void PumpVegetationLoad(VegetationGroups groups)
    {
        Marshal.WriteInt32(groups.Manager + 0x2c, unchecked(Marshal.ReadInt32(groups.Manager + 0x2c) + 1));
        Native(() => Virtual<VoidCall>(_lease.System, 0x560)(_lease.System));
        ValidateVegetationGroups(groups, false);
    }

    private int CreateVegetationDummy(VegetationGroups groups, string path, int offset)
    {
        var record = WithAscii(path, name => Native(() =>
            Terrain<CreateVegetationDummyCall>(0x109cc0)(groups.Manager, name, 0)));
        var id = record == 0 ? 0 : Marshal.ReadInt32(record);
        Marshal.WriteInt32(groups.Manager + offset, id);
        return id;
    }

    private void ValidateVegetationGroups(VegetationGroups groups, bool requireLoaded)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!ReferenceEquals(groups.Owner, this) || !ReferenceEquals(groups, _vegetationGroups) ||
            groups.Retired || (requireLoaded && !groups.Loaded))
            throw new InvalidOperationException("Vegetation requires this service's current, fully loaded group owner.");
        Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa968) == groups.Manager,
            "The native vegetation manager changed outside its captured engine generation.");
        RequireTable(groups.Manager, 0x28a7c8);
        if (groups.Loaded)
            Require(Marshal.ReadIntPtr(_objects + 0x58) == groups.VectorBegin &&
                GroupVectorCount() == groups.VectorCount,
                "The native vegetation group vector changed outside its owner.");
    }

    private int GroupVectorCount()
    {
        var begin = Marshal.ReadIntPtr(_objects + 0x58);
        var end = Marshal.ReadIntPtr(_objects + 0x60);
        Require(end >= begin && (end - begin) % 0x510 == 0 && (end - begin) / 0x510 <= 0x1800,
            "The native vegetation group vector is invalid.");
        return (int)((end - begin) / 0x510);
    }

    private void RequireIdleVegetationWorld()
    {
        RequireIdleBrushWorld();
        if (Marshal.ReadInt32(_lease.TerrainModule + 0x2fa6c0) != 0)
            throw new InvalidOperationException("Native vegetation physicalization has an in-flight owner.");
    }

    private void ValidateVegetation(Vegetation value)
    {
        ValidateVegetationGroups(value.Groups, true);
        RequireTable(value.Pointer, 0x289f88);
        Require(value.GroupId >= 0 && value.GroupId < value.Groups.VectorCount &&
            Marshal.ReadInt16(value.Pointer + 0x7c) == value.GroupId &&
            Marshal.ReadInt32(value.Pointer + 0x44) == 0,
            "The file vegetation's group or IEntity association changed outside this service.");
        if (value.ModelPointer != 0)
            Require(Marshal.ReadIntPtr(value.Groups.VectorBegin + value.GroupId * 0x510 + 8) == value.ModelPointer,
                "The owned vegetation's native model changed outside its group generation.");
    }

    private void ValidateVegetationBody(Vegetation value)
    {
        var body = value.PhysicalPointer;
        Require(body != 0 && Marshal.ReadIntPtr(value.Pointer + 0x80) == body &&
            Marshal.ReadIntPtr(body) == _lease.PhysicsModule + 0x1efe38 &&
            Native(() => _isPlaceholder(_lease.PhysicsWorld, body)) == 0 &&
            Marshal.ReadIntPtr(body + 0x78) == _lease.PhysicsWorld &&
            Marshal.ReadIntPtr(body + 0x20) == value.Pointer &&
            Marshal.ReadInt16(body + 0x28) == 1 && Marshal.ReadByte(body + 0x47) is not (5 or 7),
            "The vegetation no longer owns its exact full native static physical entity.");
    }

    private void Retire(Vegetation value)
    {
        if (value.Retired) return;
        if (value.Pointer != 0)
        {
            ValidateVegetation(value);
            RequireIdleVegetationWorld();
            var body = value.PhysicalPointer;
            if (body != 0)
            {
                ValidateVegetationBody(value);
                if (unchecked((uint)Marshal.ReadInt32(body + 0x40)) > 0x1ffff)
                    throw new InvalidOperationException("The native vegetation body has an in-flight owner; its node remains retained.");
                Native(() => Bind<DephysicalizeCall>(value.Pointer, 0x208, 0x1019f0)(value.Pointer, 0));
                Require(Marshal.ReadIntPtr(value.Pointer + 0x80) == 0 &&
                    Marshal.ReadIntPtr(body + 0x20) == 0 && Marshal.ReadInt16(body + 0x28) == -1 &&
                    Marshal.ReadByte(body + 0x47) == 7,
                    "Native vegetation dephysicalization did not synchronously detach its foreign owner.");
                try
                {
                    using var entities = new NativeGuard(_lease.PhysicsWorld + 0x57dc);
                    using var world = new NativeGuard(_lease.PhysicsWorld + 0x57c4);
                    using var queries = new NativeGuard(_lease.PhysicsWorld + 0x5804);
                    Native(() => _purgeRetiredEvents(_lease.PhysicsWorld));
                }
                catch (Exception error) { _poisoned = true; _lease.Poison(error); throw; }
                value.PhysicalPointer = 0;
            }
            else
                Require(Marshal.ReadIntPtr(value.Pointer + 0x80) == 0,
                    "The vegetation acquired unowned physics outside its resource service.");
            // 300E8240 -> 30103150 -> 301018C0 unregisters the node, clears native bending/temp
            // resources and changes the group to zero, decrementing the real nonzero group count.
            Native(() => Bind<DeleteBrushCall>(value.Pointer, 0, 0xe8240)(value.Pointer, 1));
            value.Pointer = 0;
        }
        Marshal.FreeHGlobal(value.RecordPointer);
        Marshal.FreeHGlobal(value.TranslationPointer);
        Marshal.FreeHGlobal(value.CursorPointer);
        value.RecordPointer = value.TranslationPointer = value.CursorPointer = 0;
        value.Retired = true;
        _vegetation.Remove(value);
    }

    private void Retire(VegetationGroups groups, string diagnosticPath)
    {
        if (groups.Retired) return;
        ValidateVegetationGroups(groups, false);
        if (_vegetation.Any(value => !value.Retired && ReferenceEquals(value.Groups, groups)))
        {
            // TEMPORARY diagnosis: the guard is unreachable unless the eager instance retirement
            // never ran or could not finish. Record which one before throwing.
            RetirementDiagnostic.VegetationGroupGuard(this, groups, diagnosticPath);
            throw new InvalidOperationException("Retire all vegetation instances before their group definitions.");
        }
        RequireIdleVegetationWorld();
        if (groups.Loaded)
            for (var index = 0; index < groups.VectorCount; index++)
                Require(Marshal.ReadInt32(groups.VectorBegin + index * 0x510 + 0x500) == groups.ReferenceCounts[index],
                    "The native vegetation group has references outside this owner.");
        ReleaseVegetationLoadInputs(groups);
        // Reset clears descriptor map ownership first. The real vector shrink runs every
        // StatInstGroup destructor, releasing actual model, material, string and derived refs.
        Native(() => Bind<VoidCall>(groups.Manager, 8, 0x109650)(groups.Manager));
        Native(() => Terrain<ResizeVegetationGroupsCall>(0x14ec20)(_objects + 0x58, 0));
        Require(GroupVectorCount() == 0, "The native vegetation groups did not retire completely.");
        groups.Retired = true;
        _vegetationGroups = null;
    }

    private void RetireVegetationResources()
    {
        var diagnosticId = RetirementDiagnostic.Enter(nameof(RetireVegetationResources)); // TEMPORARY.
        Exception? diagnosticError = null;
        try
        {
            foreach (var value in _vegetation.ToArray()) Retire(value);
            if (_vegetationGroups is not null) Retire(_vegetationGroups, nameof(RetireVegetationResources));
        }
        catch (Exception error) { diagnosticError = error; throw; }
        finally { RetirementDiagnostic.Exit(diagnosticId, nameof(RetireVegetationResources), diagnosticError); }
    }

    private void DisposeVegetation(Vegetation value)
    {
        if (value.Retired) return;
        using var operation = Enter();
        Retire(value);
    }

    private void DisposeVegetationGroups(VegetationGroups groups)
    {
        if (groups.Retired) return;
        var diagnosticId = RetirementDiagnostic.Enter(nameof(DisposeVegetationGroups)); // TEMPORARY.
        try
        {
            using var operation = Enter();
            Retire(groups, "DisposeVegetationGroups");
        }
        catch (Exception error) { RetirementDiagnostic.Exit(diagnosticId, nameof(DisposeVegetationGroups), error); throw; }
        RetirementDiagnostic.Exit(diagnosticId, nameof(DisposeVegetationGroups), null);
    }

    private void DestroyVegetationDescriptor(VegetationGroups groups)
    {
        if (!groups.DescriptorInitialized) return;
        Native(() => Terrain<VoidCall>(0x10d1c0)(groups.Descriptor));
        groups.DescriptorInitialized = false;
    }

    private void ReleaseVegetationLoadInputs(VegetationGroups groups)
    {
        DestroyVegetationDescriptor(groups);
        if (groups.XmlHandles != 0)
        {
            ReleaseVegetationXml(groups.XmlHandles + 16);
            ReleaseVegetationXml(groups.XmlHandles + 8);
            ReleaseVegetationXml(groups.XmlHandles);
        }
        Marshal.FreeHGlobal(groups.Descriptor);
        Marshal.FreeHGlobal(groups.XmlHandles);
        Marshal.FreeHGlobal(groups.PathPointer);
        groups.Descriptor = groups.XmlHandles = groups.PathPointer = 0;
    }

    private void ReleaseVegetationXml(nint handle)
    {
        var node = Marshal.ReadIntPtr(handle);
        if (node == 0) return;
        Native(() => Virtual<ReferenceCall>(node, 8)(node));
        Marshal.WriteIntPtr(handle, 0);
    }

    private T Terrain<T>(int rva) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(_lease.TerrainModule + rva);

    private T Virtual<T>(nint instance, int slot) where T : Delegate
    {
        Require(instance != 0 && Marshal.ReadIntPtr(instance) != 0 &&
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + slot) != 0,
            "The real native vegetation/XML service lacks its required virtual operation.");
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + slot));
    }

    private T WithAscii<T>(string text, Func<nint, T> action)
    {
        var pointer = Allocate(PathBytes(text));
        try { return action(pointer); }
        finally { if (!_poisoned) Marshal.FreeHGlobal(pointer); }
    }

    private void WithAscii(string text, Action<nint> action) =>
        WithAscii(text, pointer => { action(pointer); return 0; });

    public sealed class VegetationGroups : IDisposable
    {
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal int OwnerThread { get; }
        internal nint Manager, PathPointer, XmlHandles, Descriptor, VectorBegin;
        internal bool DescriptorInitialized, Loaded, Retired;
        internal int VectorCount;
        internal int[] ReferenceCounts = [];
        public ZoneWorldMapContent World { get; }
        public string Path { get; }
        public int AuthoredRowCount { get; internal set; }
        public int DefinitionCount { get; internal set; }
        public int StreamDummyGroupId { get; internal set; }
        public int DefaultDummyGroupId { get; internal set; }
        public bool IsDisposed => Retired;
        internal VegetationGroups(ZoneNativeStaticObjectServices owner, ZoneWorldMapContent world, string path, nint manager)
        { Owner = owner; World = world; Path = path; Manager = manager; OwnerThread = Environment.CurrentManagedThreadId; }
        public void Dispose() => Owner.DisposeVegetationGroups(this);
    }

    /// <summary>One actual CVegetation with native group retention and optional static physics.</summary>
    public sealed class Vegetation : IDisposable
    {
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal VegetationGroups Groups { get; }
        internal nint Pointer, PhysicalPointer, ModelPointer, RecordPointer, TranslationPointer, CursorPointer;
        internal bool Retired;
        public short GroupId { get; internal set; }
        public Vector3 NativePosition { get; internal set; }
        public float NativeScale { get; internal set; }
        public bool ModelWasFallback { get; internal set; }
        public bool HasPhysicalEntity => !Retired && PhysicalPointer != 0;
        public byte? PhysicalSimulationClass { get; internal set; }
        public bool IsCellPublicationComplete { get; internal set; }
        public byte? NativeCellPublicationByte { get; internal set; }
        public bool IsDisposed => Retired;
        internal Vegetation(ZoneNativeStaticObjectServices owner, VegetationGroups groups)
        { Owner = owner; Groups = groups; }
        public void Dispose() => Owner.DisposeVegetation(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LoadVegetationXmlCall(nint system, nint result, nint path, byte reuseStrings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint FindVegetationXmlCall(nint node, nint result, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VegetationXmlChildCall(nint node, nint result, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte VegetationAttributeCall(nint node, nint name, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ParseVegetationGroupCall(nint manager, nint nodeRef, nint descriptor, byte read);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VegetationMapEntryCall(nint map, nint key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CopyVegetationGroupCall(nint destination, nint source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PublishVegetationGroupCall(nint manager, nint descriptor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateVegetationDummyCall(nint manager, nint path, byte bending);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResizeVegetationGroupsCall(nint vector, ulong count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateVegetationCall(nint translation, nint cursor, byte skip);
}
