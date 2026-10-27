using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.StaticObjects;

/// <summary>
/// Retains real CStatObj and material resources loaded by the dedicated engine in one CSystem
/// generation. All calls run on the engine dispatcher under its shared operation gate. A loaded
/// model, including the native default-model fallback, does not establish cell collision readiness.
/// </summary>
public sealed unsafe partial class ZoneNativeStaticObjectServices : IDisposable
{
    internal ZoneNativeEngine Engine => _lease.Engine;
    internal void VerifyResourceOwner()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lease.ThrowIfFaulted();
    }

    internal nint CaptureOutdoorCellObjectRoot()
    {
        using var operation = Enter();
        var root = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5dc0);
        if (root == 0)
            throw new InvalidOperationException("Outdoor cell objects require the engine's initialized native octree root.");
        // COctreeNode+0 is its borrowed CVisArea association, copied by3021BC80.
        // Outdoor roots legitimately have no area; a missing root is a different state.
        if (Marshal.ReadIntPtr(root) != 0)
            throw new NotSupportedException("Indoor cell objects require their visibility area's publication and retirement owner.");
        return root;
    }
    private readonly ZoneNativeEngine.StaticObjectLease _lease;
    private readonly nint _objects;
    private readonly nint _materials;
    private readonly nint _surfaces;
    private readonly nint _cellPublicationService;
    private readonly nint _cellPublicationVtable;
    private readonly nint _cellPublicationFunction;
    private readonly LoadModelCall _loadModel;
    private readonly LoadMaterialCall _loadMaterial;
    private readonly CreateBrushCall _createBrush;
    private readonly PlaceholderCall _isPlaceholder;
    private readonly VoidCall _purgeRetiredEvents;
    private readonly ByteCall? _cellPublicationByte;
    private readonly List<Model> _models = [];
    private readonly List<Material> _loadedMaterials = [];
    private readonly List<Brush> _brushes = [];
    private bool _disposed;
    private bool _poisoned;

    internal ZoneNativeStaticObjectServices(ZoneNativeEngine.StaticObjectLease lease)
    {
        _lease = lease;
        // Construction performs no native allocation or callback. The engine roots Resource
        // before any caller can load a model or acquire a native reference.
        _objects = Marshal.ReadIntPtr(lease.TerrainModule + 0x2fa8b8);
        _materials = Marshal.ReadIntPtr(lease.TerrainModule + 0x2fa928);
        Require(_objects != 0 && _materials != 0,
            "The initialized engine has no object or material manager.");
        RequireTable(lease.ThreeDEngine, 0x28d408);
        RequireTable(_materials, 0x297e88);
        _surfaces = Marshal.ReadIntPtr(_materials + 0xe0);
        RequireTable(_surfaces, 0x298090);
        _cellPublicationService = Marshal.ReadIntPtr(lease.Environment + 0x108);
        // This later publication capability is optional for raw resource loading. Absence is a
        // recoverable admission failure when needed, never a reason to pin healthy model refs.
        if (_cellPublicationService != 0)
        {
            _cellPublicationVtable = Marshal.ReadIntPtr(_cellPublicationService);
            if (_cellPublicationVtable != 0)
            {
                _cellPublicationFunction = Marshal.ReadIntPtr(_cellPublicationVtable + 0x38);
                if (_cellPublicationFunction != 0)
                    _cellPublicationByte = Marshal.GetDelegateForFunctionPointer<ByteCall>(_cellPublicationFunction);
            }
        }
        Require(Marshal.ReadIntPtr(lease.Environment + 0x98) != 0,
            "Static-model loading requires the engine's real CryPak service.");
        _loadModel = Marshal.GetDelegateForFunctionPointer<LoadModelCall>(lease.TerrainModule + 0x120e40);
        _loadMaterial = Bind<LoadMaterialCall>(_materials, 0x30, 0x1c8570);
        _createBrush = Marshal.GetDelegateForFunctionPointer<CreateBrushCall>(lease.TerrainModule + 0x21e840);
        _isPlaceholder = Marshal.GetDelegateForFunctionPointer<PlaceholderCall>(lease.PhysicsModule + 0x760c0);
        _purgeRetiredEvents = Marshal.GetDelegateForFunctionPointer<VoidCall>(lease.PhysicsModule + 0x79b30);
    }

    /// <summary>
    /// Calls the actual object-table loader's CObjManager::LoadStatObj(path, null, null, 0, 1, 0,
    /// 0, 0), then retains its returned CStatObj. Null and default-CGF fallback are distinct native
    /// outcomes. Streaming, CGF parsing, subobjects, materials and physical geometry remain owned
    /// by that loader; this method does not build collision from a model's bounds.
    /// </summary>
    public Model? LoadModel(string path)
    {
        using var operation = Enter();
        var bytes = PathBytes(path);
        var model = new Model(this, path);
        _models.Add(model); // Keep partial native ownership rooted before invoking the loader.
        try
        {
            model.PathPointer = Allocate(bytes);
            model.Pointer = Native(() => _loadModel(_objects, model.PathPointer, 0, 0, 0, 1, 0, 0, 0));
            if (model.Pointer == 0)
            {
                Retire(model);
                return null;
            }
            RequireTable(model.Pointer, 0x27ee78);
            Native(() => Bind<ReferenceCall>(model.Pointer, 0x38, 0x244c0)(model.Pointer));
            model.ReferenceHeld = true;
            model.IsFallback = model.Pointer == Marshal.ReadIntPtr(_objects + 0x168);
            model.IsEmptyObject = path == "NOFILE";
            // 3002F9A0 reads the actual native subobject vector's count. The geometry vector is
            // the same 16-byte (physical-geometry pointer, type) table read by CBrush::Physicalize.
            model.SubObjectCount = Native(() => Bind<ReferenceCall>(model.Pointer, 0x1f8, 0x2f9a0)(model.Pointer));
            var begin = Marshal.ReadIntPtr(model.Pointer + 0x148);
            var end = Marshal.ReadIntPtr(model.Pointer + 0x150);
            Require(end >= begin && (end - begin) % 16 == 0 && (end - begin) / 16 <= int.MaxValue,
                "The loaded CStatObj has an invalid physical-geometry vector.");
            var count = 0;
            for (var entry = begin; entry != end; entry += 16)
                if (Marshal.ReadIntPtr(entry) != 0) count++;
            model.PhysicalGeometryCount = count;
            return model;
        }
        catch (Exception error)
        {
            if (_poisoned) throw; // Uncertain native completion pins the engine and inputs.
            try { Retire(model); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Loads and retains the authored material through the initialized CMatMan, including its
    /// real surface manager. The native default-material result is returned with IsFallback set.
    /// Empty resource-table names are skipped by the caller, as in the native table loader.
    /// </summary>
    public Material? LoadMaterial(string path)
    {
        using var operation = Enter();
        var bytes = PathBytes(path);
        var material = new Material(this, path);
        _loadedMaterials.Add(material);
        try
        {
            material.PathPointer = Allocate(bytes);
            material.Pointer = Native(() => _loadMaterial(_materials, material.PathPointer, 1, 0, 0));
            if (material.Pointer == 0)
            {
                Retire(material);
                return null;
            }
            RequireTable(material.Pointer, 0x297378);
            Native(() => Bind<ReferenceCall>(material.Pointer, 8, 0x1c4070)(material.Pointer));
            material.ReferenceHeld = true;
            material.IsFallback = material.Pointer == Marshal.ReadIntPtr(_materials + 0x78);
            return material;
        }
        catch (Exception error)
        {
            if (_poisoned) throw;
            try { Retire(material); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Invokes the actual _LoadBrush factory on one packed 128-byte type-one payload (without its
    /// type tag). It applies the native layer/configuration gates, cell translation, resource
    /// retention, registration and physicalization. A null result means native admission skipped
    /// this brush; a returned brush can still have no physical entity. Neither result declares a
    /// cell ready. Models and materials must belong to this service; their temporary table refs
    /// may be disposed immediately afterward because CBrush retains its own native references.
    /// </summary>
    public Brush? CreateBrush(Vector3 cellTranslation, ReadOnlySpan<byte> rawPayload, Model? model, Material? material)
    {
        using var operation = Enter();
        _lease.RequireTerrainEntityGrid();
        if (rawPayload.Length != 128)
            throw new ArgumentException("A native brush payload must contain exactly 128 bytes.", nameof(rawPayload));
        if (!float.IsFinite(cellTranslation.X) || !float.IsFinite(cellTranslation.Y) || !float.IsFinite(cellTranslation.Z))
            throw new ArgumentException("The engine cell translation must be finite.", nameof(cellTranslation));
        if (model is not null && (!ReferenceEquals(model.Owner, this) || model.Retired))
            throw new InvalidOperationException("The brush model must be a live reference from this static-object service.");
        if (material is not null && (!ReferenceEquals(material.Owner, this) || material.Retired))
            throw new InvalidOperationException("The brush material must be a live reference from this static-object service.");
        // Real RegisterEntity3013EDE0 also uses this service before cell publication metadata.
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        RequireIdleBrushWorld(); // Admission guarantees this owner's proven retirement path is available.
        if (model is not null) RequireTable(model.Pointer, 0x27ee78);
        if (material is not null) RequireTable(material.Pointer, 0x297378);
        var brush = new Brush(this, model, material);
        _brushes.Add(brush);
        try
        {
            brush.RecordPointer = Allocate(rawPayload.ToArray());
            brush.TranslationPointer = Marshal.AllocHGlobal(12);
            *(Vector3*)brush.TranslationPointer = cellTranslation;
            brush.Pointer = Native(() => _createBrush(brush.TranslationPointer, brush.RecordPointer,
                brush.ModelPointer, brush.MaterialPointer));
            if (brush.Pointer == 0)
            {
                Retire(brush);
                return null;
            }
            ValidateBrush(brush);
            brush.PhysicalPointer = Marshal.ReadIntPtr(brush.Pointer + 0x68);
            if (brush.PhysicalPointer != 0)
            {
                ValidateBrushBody(brush);
                brush.PhysicalSimulationClass = Marshal.ReadByte(brush.PhysicalPointer + 0x47);
            }
            return brush;
        }
        catch (Exception error)
        {
            if (_poisoned) throw;
            try { Retire(brush); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Completes 30225050's serialized-cell metadata after _LoadBrush, before the caller adds
    /// this brush to its owned cell collection. Calls the real gEnv+108 virtual+38 byte getter,
    /// then sets CBrush+39 bit zero and copies the returned byte to +38 in native order.
    /// This does not insert into the native cell vector, whose lifetime the managed owner supplies.
    /// </summary>
    public void CompleteCellBrush(Brush brush)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(brush);
        if (!ReferenceEquals(brush.Owner, this) || brush.Retired)
            throw new InvalidOperationException("Cell publication requires a live brush from this static-object service.");
        if (brush.IsCellPublicationComplete)
            throw new InvalidOperationException("This brush has already completed serialized-cell publication metadata.");
        RequireCellPublicationAvailable();
        ValidateBrush(brush);
        ValidateCellPublicationService();
        var value = Native(() => _cellPublicationByte!(_cellPublicationService));
        ValidateCellPublicationService();
        ValidateBrush(brush);
        Native(() =>
        {
            Marshal.WriteByte(brush.Pointer + 0x39, (byte)(Marshal.ReadByte(brush.Pointer + 0x39) | 1));
            Marshal.WriteByte(brush.Pointer + 0x38, value);
        });
        brush.NativeCellPublicationByte = value;
        brush.IsCellPublicationComplete = true;
    }

    /// <summary>
    /// Gate inputs for the deferred region gate 30172560, bound from recorded evidence instead of
    /// native memory: the scene's active position with the exact provenance it was bound from, and
    /// the activation-radius value in its recorded dedicated form. No native pointer and no native
    /// accessor call survives this record.
    /// </summary>
    internal sealed record ZoneCellDeferredGateContext(Vector3 ActivePosition, string PositionSource, float RawRadiusValue);

    /// <summary>
    /// Binds the two engine inputs the native deferred blocking pass region gate 30172560
    /// evaluates, from recorded evidence instead of unverified native memory:
    ///
    /// - ActivePosition (+0.0, +0.0, +0.0): the dedicated activation inputs are proved to be mode 0
    ///   with initial world position (+0.0,+0.0,+0.0); constructor 30508D90 supplies the initial
    ///   zero vector and the registered zone command/task never calls the position setter
    ///   (FIRST_ENTRY_NOTES.md:824-829, restated :915-916; TODO.md:130). This scene never activates
    ///   native Seamless LoadZone365704D0 (ZoneSceneActivation.cs:41-42), spawns no actor and runs
    ///   no camera/view, so nothing moves that position before this load-time gate, and the
    ///   recovered 30172560 evaluation itself runs at engine-local zero, where all 6,389 authored
    ///   records of the six target cells activate (FIRST_ENTRY_NOTES.md:338-340).
    /// - Radius: the dedicated branch's constants recovered from cry3dengine_dedicate.dll bytes,
    ///   R^2 = 1e9 with fVar23 = 1.0, activation radius near 31,623 units
    ///   (FIRST_ENTRY_NOTES.md:336-338).
    ///
    /// The previously claimed seam - Seamless virtual vtbl+0x128 (origin at +0x18 of the returned
    /// record) plus the 3DEngine+0x3a8 radius word (FIRST_ENTRY_NOTES.md:321-322) - is deliberately
    /// NOT used: runner batch b3-20260922 proved the vtbl+0x128 call faults with 0xC0000005 inside
    /// the callee even after every structural identity check passed, and neither offset's exact
    /// call/read ABI is recorded anywhere else in the evidence corpus. A gate input without
    /// recorded evidence refuses here by name, with the exact missing evidence; the caller
    /// converts that into the named pass refusal. The only native access below is the shared
    /// generation gate Enter() (ZoneNativeStaticObjectServices.cs:412-428), whose manager-identity
    /// reads (:416-422) are the same ones LoadModel/LoadMaterial/CreateBrush already executed
    /// successfully in that batch (runtime-proven by the five preceding passes of b3-20260922:
    /// MainObjects, SectorVegetation, Visibility, XmlEntities, BigObjects all reached this call
    /// site's neighbours through the same gate).
    /// </summary>
    internal ZoneCellDeferredGateContext ReadDeferredGateContext(ZoneNativeWorldLayout selection)
    {
        if (selection is null)
            throw new NotSupportedException(
                "The deferred region gate has no scene-resolved world layout: missing evidence is the " +
                "ZoneNativeWorldLayout issued by ZoneNativeEngine.ResolveWorldLayout (ZoneNativeEngine.WorldLayout.cs:14-41), " +
                "which owns this pass's World name/origin binding (FIRST_ENTRY_NOTES.md:316-322).");
        if (!ReferenceEquals(selection.Owner, Engine))
            throw new NotSupportedException(
                "The deferred region gate's world layout belongs to another engine generation: missing evidence is a " +
                "ZoneNativeWorldLayout resolved by this service's own engine.");
        var worldName = selection.Layout.World.Header.Name;
        if (string.IsNullOrEmpty(worldName) || selection.Layout.Cells.Count == 0)
            throw new NotSupportedException(
                "The scene-resolved layout carries no World name or cells: missing evidence is the authored world header " +
                "validated against the native registry by ZoneNativeEngine.ValidateWorldLayout " +
                "(ZoneNativeEngine.WorldLayout.cs:63, :78-82, :86-88).");
        using var operation = Enter();
        var origin = selection.Layout.Origin;
        return new(DedicatedInitialPosition,
            $"scene binding: proved dedicated activation position (+0.0,+0.0,+0.0) " +
            $"(FIRST_ENTRY_NOTES.md:824-829/:915-916, gate evaluated at engine-local zero :338-340); " +
            $"radius constant R^2=1e9 (FIRST_ENTRY_NOTES.md:336-338); world '{worldName}' origin ({origin.X},{origin.Y}) " +
            $"from the resolved layout",
            DedicatedRadiusSquared);
    }

    // Proved dedicated activation position (FIRST_ENTRY_NOTES.md:824-829/:915-916, TODO.md:130):
    // constructor 30508D90 supplies the initial zero vector, the registered zone command/task
    // never calls the position setter, and this scene never runs native Seamless LoadZone365704D0
    // (ZoneSceneActivation.cs:41-42). If a future scene ever binds an active position, it must
    // supply it here - never read it back from unverified native memory.
    private static readonly Vector3 DedicatedInitialPosition = new(0f, 0f, 0f);

    // Recorded dedicated-branch radius constant: R^2 = 1e9 recovered from cry3dengine_dedicate.dll
    // bytes with fVar23 = 1.0 (activation radius near 31,623 units), FIRST_ENTRY_NOTES.md:336-338.
    private const float DedicatedRadiusSquared = 1e9f;

    /// <summary>
    /// TEMPORARY instrumentation hook: sample the native visibility deletion queues at a load
    /// boundary so the caller can attribute pending entries to a load phase. Read-only, and a no-op
    /// unless the retirement diagnostic is enabled. Delete with the diagnostic.
    /// </summary>
    internal void SampleVisibilityQueues(string pathTag) =>
        RetirementDiagnostic.QueueSample(this, pathTag);

    public void Dispose()
    {
        if (_disposed) return;
        var diagnosticId = RetirementDiagnostic.Enter(nameof(Dispose));
        // TEMPORARY retirement-diagnostic step record.
        var diagnosticSteps = new List<(string Name, Exception? Error)>();
        try
        {
            using var operation = Enter();
            // Every step runs even when an earlier one failed, and all failures are reported
            // together. The previous shape let one step's exception abandon the other nine, which
            // stranded 4603 vegetation instances and 17 imported visibility batches and then
            // produced two misleading downstream failures. ZoneSceneActivation.Dispose already
            // collects this way; this owner now matches it.
            void RetireStep(string phase, Action action)
            {
                try { action(); diagnosticSteps.Add((phase, null)); }
                catch (Exception error) { diagnosticSteps.Add((phase, error)); }
            }
            RetireStep("RetireVisibilityCellResources", RetireVisibilityCellResources);
            RetireStep("RetireVisibilityManagerResources", RetireVisibilityManagerResources);
            RetireStep("RetireVegetationResources", RetireVegetationResources);
            RetireStep("RetireDecalResources", RetireDecalResources);
            RetireStep("RetireRoadResources", RetireRoadResources);
            RetireStep("RetireWaterResources", RetireWaterResources);
            RetireStep("RetireBrushes", () => { foreach (var brush in _brushes.ToArray()) Retire(brush); });
            RetireStep("RetireMaterials", () => { foreach (var material in _loadedMaterials.ToArray()) Retire(material); });
            RetireStep("RetireModels", () => { foreach (var model in _models.ToArray()) Retire(model); });
            // The lease is released only when nothing above failed: a failed step still owns
            // native resources, and releasing the lease would let the engine unload underneath
            // allocations that were never retired. Keeping it retains the engine, which is the
            // documented retain-on-uncertainty behaviour for an incomplete retirement.
            if (diagnosticSteps.All(step => step.Error is null))
            {
                _lease.Release();
                diagnosticSteps.Add(("ReleaseLease", null));
                _disposed = true;
            }
            else
            {
                diagnosticSteps.Add(("ReleaseLease", new InvalidOperationException(
                    "Not released: an earlier retirement step failed and still owns native resources.")));
            }
        }
        finally
        {
            RetirementDiagnostic.StaticsDisposeSteps(this, diagnosticSteps);
            var first = diagnosticSteps.FirstOrDefault(step => step.Error is not null).Error;
            RetirementDiagnostic.Exit(diagnosticId, nameof(Dispose), first);
        }
        var failures = diagnosticSteps.Where(step => step.Error is not null).Select(step => step.Error!).ToArray();
        if (failures.Length != 0)
            throw new AggregateException("Static-object retirement failed.", failures);
    }

    // TEMPORARY retirement-diagnostic helper; delete with the instrumentation.
    private static string StepName(int index) => index switch
    {
        0 => "RetireVisibilityCellResources",
        1 => "RetireVisibilityManagerResources",
        2 => "RetireVegetationResources",
        3 => "RetireDecalResources",
        4 => "RetireRoadResources",
        5 => "RetireWaterResources",
        6 => "RetireBrushes",
        7 => "RetireMaterials",
        8 => "RetireModels",
        9 => "ReleaseLease",
        _ => $"unknown({index})",
    };

    private IDisposable Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = _lease.Enter();
        try
        {
            Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8b8) == _objects &&
                Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa928) == _materials,
                "Static-model managers no longer belong to the captured engine generation.");
            RequireTable(_materials, 0x297e88);
            Require(Marshal.ReadIntPtr(_materials + 0xe0) == _surfaces,
                "The native material surface manager has been replaced.");
            RequireTable(_surfaces, 0x298090);
            return operation;
        }
        catch { operation.Dispose(); throw; }
    }

    private void DisposeModel(Model model)
    {
        if (model.Retired) return;
        using var operation = Enter();
        Retire(model);
    }

    private void DisposeMaterial(Material material)
    {
        if (material.Retired) return;
        using var operation = Enter();
        Retire(material);
    }

    private void DisposeBrush(Brush brush)
    {
        if (brush.Retired) return;
        using var operation = Enter();
        Retire(brush);
    }

    private void ValidateBrush(Brush brush)
    {
        RequireTable(brush.Pointer, 0x28aaa8);
        Require(Marshal.ReadIntPtr(brush.Pointer + 0x58) == brush.ModelPointer &&
            Marshal.ReadIntPtr(brush.Pointer + 0x60) == brush.MaterialPointer &&
            Marshal.ReadInt32(brush.Pointer + 0x44) == 0,
            "The owned file brush's resource or IEntity association changed outside this service.");
    }

    private void ValidateCellPublicationService()
    {
        // Check current environment ownership before dereferencing the captured service.
        Require(Marshal.ReadIntPtr(_lease.Environment + 0x108) == _cellPublicationService &&
            Marshal.ReadIntPtr(_cellPublicationService) == _cellPublicationVtable &&
            Marshal.ReadIntPtr(_cellPublicationVtable + 0x38) == _cellPublicationFunction,
            "The native cell-publication state service changed outside its engine generation.");
    }

    private void RequireCellPublicationAvailable()
    {
        if (_cellPublicationByte is null)
            throw new NotSupportedException("This engine has no captured native cell-publication state service; model/material references can still be retired normally.");
    }

    private void ValidateBrushBody(Brush brush)
    {
        var body = brush.PhysicalPointer;
        // _LoadBrush -> CBrush::Physicalize -> World+B0(type1) uses the full 0x1c0
        // CPhysicalEntity constructor, never the distinct B8 placeholder allocator.
        Require(body != 0 && Marshal.ReadIntPtr(brush.Pointer + 0x68) == body &&
            Marshal.ReadIntPtr(body) == _lease.PhysicsModule + 0x1efe38 &&
            Native(() => _isPlaceholder(_lease.PhysicsWorld, body)) == 0 &&
            Marshal.ReadIntPtr(body + 0x78) == _lease.PhysicsWorld &&
            Marshal.ReadIntPtr(body + 0x20) == brush.Pointer && Marshal.ReadInt16(body + 0x28) == 1 &&
            Marshal.ReadByte(body + 0x47) is not (5 or 7),
            "The brush no longer owns its exact full native static physical entity.");
    }

    private void RequireIdleBrushWorld()
    {
        var world = _lease.PhysicsWorld;
        // This component is deliberately bound to the engine's current pre-update phase.
        // 35083830's ordinary mode-zero call cannot enqueue destruction or notify an old-foreign
        // listener when dt == 0 and these guards are idle. Stepped worlds require a later owned
        // drain integration; do not turn their queued destruction into a freed CBrush pointer.
        if (*(float*)(world + 0x1a4) != 0 || Marshal.ReadInt32(world + 0x57dc) != 0 ||
            Marshal.ReadInt32(world + 0x57c4) != 0 || Marshal.ReadInt32(world + 0x5804) != 0)
            throw new InvalidOperationException("Brush ownership requires the idle pre-update native world; retained brushes cannot retire during a step or query.");
    }

    private void Retire(Brush brush)
    {
        if (brush.Retired) return;
        if (brush.Pointer != 0)
        {
            ValidateBrush(brush);
            var body = brush.PhysicalPointer;
            if (body != 0)
            {
                RequireIdleBrushWorld();
                ValidateBrushBody(brush);
                if (unchecked((uint)Marshal.ReadInt32(body + 0x40)) > 0x1ffff)
                    throw new InvalidOperationException("The native brush body has an in-flight owner; its brush remains retained.");
                // No native writer guards here: mode-zero destruction acquires its own guards.
                // Native 3010EB00, not a managed pointer write, clears CBrush+68 on success.
                Native(() => Bind<DephysicalizeCall>(brush.Pointer, 0x208, 0x10eb00)(brush.Pointer, 0));
                Require(Marshal.ReadIntPtr(brush.Pointer + 0x68) == 0 &&
                    Marshal.ReadIntPtr(body + 0x20) == 0 && Marshal.ReadInt16(body + 0x28) == -1 &&
                    Marshal.ReadByte(body + 0x47) == 7,
                    "Native brush dephysicalization did not synchronously detach its foreign owner.");
                // Full retired entities remain on the native delayed-free list. Remove logged
                // events containing them before freeing the foreign CBrush owner; dispatch none.
                try
                {
                    using var entities = new NativeGuard(_lease.PhysicsWorld + 0x57dc);
                    using var world = new NativeGuard(_lease.PhysicsWorld + 0x57c4);
                    using var queries = new NativeGuard(_lease.PhysicsWorld + 0x5804);
                    Native(() => _purgeRetiredEvents(_lease.PhysicsWorld));
                }
                catch (Exception error)
                {
                    // The body has already retired. Keep its brush and event roots pinned if
                    // the final purge cannot complete; replaying physicalization is not cleanup.
                    _poisoned = true;
                    _lease.Poison(error);
                    throw;
                }
                brush.PhysicalPointer = 0;
            }
            else
                Require(Marshal.ReadIntPtr(brush.Pointer + 0x68) == 0,
                    "The brush acquired unowned physics outside its resource service.");
            // 30112470 -> 3010EA30 unregisters the real render/octree node and releases its
            // independent model/material refs. Its repeated dephysicalize sees native null.
            Native(() => Bind<DeleteBrushCall>(brush.Pointer, 0, 0x112470)(brush.Pointer, 1));
            brush.Pointer = 0;
        }
        Marshal.FreeHGlobal(brush.RecordPointer);
        Marshal.FreeHGlobal(brush.TranslationPointer);
        brush.RecordPointer = 0;
        brush.TranslationPointer = 0;
        brush.Retired = true;
        _brushes.Remove(brush);
    }

    private void Retire(Model model)
    {
        if (model.Retired) return;
        if (model.ReferenceHeld)
        {
            RequireTable(model.Pointer, 0x27ee78);
            // CStatObj::Release schedules the real CObjManager collection when necessary.
            // The still-live engine owns that queue; never delete a cached CStatObj directly.
            Native(() => Bind<ReferenceCall>(model.Pointer, 0x40, 0x244e0)(model.Pointer));
            model.ReferenceHeld = false;
        }
        model.Pointer = 0;
        Marshal.FreeHGlobal(model.PathPointer);
        model.PathPointer = 0;
        model.Retired = true;
        _models.Remove(model);
    }

    private void Retire(Material material)
    {
        if (material.Retired) return;
        if (material.ReferenceHeld)
        {
            RequireTable(material.Pointer, 0x297378);
            Native(() => Bind<VoidCall>(material.Pointer, 0x10, 0x1c4080)(material.Pointer));
            material.ReferenceHeld = false;
        }
        material.Pointer = 0;
        Marshal.FreeHGlobal(material.PathPointer);
        material.PathPointer = 0;
        material.Retired = true;
        _loadedMaterials.Remove(material);
    }

    private T Native<T>(Func<T> call)
    {
        try
        {
            var result = call();
            _lease.ThrowIfFaulted();
            return result;
        }
        catch (Exception error) { _poisoned = true; _lease.Poison(error); throw; }
    }

    private void Native(Action call) => Native(() => { call(); return 0; });

    private T Bind<T>(nint instance, int slot, int offset) where T : Delegate
    {
        var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + slot);
        Require(address == _lease.TerrainModule + offset,
            "A static-object virtual no longer matches the admitted dedicated engine.");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void RequireTable(nint instance, int offset) => Require(instance != 0 &&
        Marshal.ReadIntPtr(instance) == _lease.TerrainModule + offset,
        "A static-object service or resource has an unexpected native type.");

    private void Require(bool condition, string message)
    {
        if (condition) return;
        var error = new InvalidOperationException(message);
        _poisoned = true;
        _lease.Poison(error);
        throw error;
    }

    private static byte[] PathBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Native object tables contain first-NUL-terminated 256-byte ASCII paths. Do not pass
        // strings that overflow that authored contract or silently replace non-ASCII bytes.
        if (path.Length > 255 || path.Any(character => character is '\0' or > '\x7f'))
            throw new ArgumentException("A static resource path must contain at most 255 non-NUL ASCII bytes.", nameof(path));
        return Encoding.ASCII.GetBytes(path + '\0');
    }

    private static nint Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    /// <summary>One retained native CStatObj reference; all counts are copied at load completion.</summary>
    public sealed class Model : IDisposable
    {
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, PathPointer;
        internal bool ReferenceHeld, Retired;
        public string Path { get; }
        public bool IsFallback { get; internal set; }
        public bool IsEmptyObject { get; internal set; }
        public int PhysicalGeometryCount { get; internal set; }
        public int SubObjectCount { get; internal set; }
        public bool IsDisposed => Retired;
        internal Model(ZoneNativeStaticObjectServices owner, string path) { Owner = owner; Path = path; }
        public void Dispose() => Owner.DisposeModel(this);
    }

    public sealed class Material : IDisposable
    {
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, PathPointer;
        internal bool ReferenceHeld, Retired;
        public string Path { get; }
        public bool IsFallback { get; internal set; }
        public bool IsDisposed => Retired;
        internal Material(ZoneNativeStaticObjectServices owner, string path) { Owner = owner; Path = path; }
        public void Dispose() => Owner.DisposeMaterial(this);
    }

    /// <summary>
    /// One real native file brush, including its own resource refs and optional physical entity.
    /// Retire on the engine dispatcher before CSystem, while the native world remains pre-update.
    /// </summary>
    public sealed class Brush : IDisposable
    {
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, PhysicalPointer, RecordPointer, TranslationPointer;
        internal readonly nint ModelPointer, MaterialPointer;
        internal bool Retired;
        public string? ModelPath { get; }
        public string? MaterialPath { get; }
        public bool ModelWasFallback { get; }
        public bool MaterialWasFallback { get; }
        public bool HasPhysicalEntity => !Retired && PhysicalPointer != 0;
        public byte? PhysicalSimulationClass { get; internal set; }
        public bool IsCellPublicationComplete { get; internal set; }
        public byte? NativeCellPublicationByte { get; internal set; }
        public bool IsDisposed => Retired;
        internal Brush(ZoneNativeStaticObjectServices owner, Model? model, Material? material)
        {
            Owner = owner;
            ModelPointer = model?.Pointer ?? 0;
            MaterialPointer = material?.Pointer ?? 0;
            ModelPath = model?.Path;
            MaterialPath = material?.Path;
            ModelWasFallback = model?.IsFallback ?? false;
            MaterialWasFallback = material?.IsFallback ?? false;
        }
        public void Dispose() => Owner.DisposeBrush(this);
    }

    private readonly struct NativeGuard : IDisposable
    {
        private readonly nint _address;
        internal NativeGuard(nint address)
        {
            if (Interlocked.CompareExchange(ref *(int*)address, 0x10000, 0) != 0)
                throw new InvalidOperationException("Native physics has an unexpected in-flight guard owner.");
            _address = address;
        }
        public void Dispose() => Interlocked.Add(ref *(int*)_address, -0x10000);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LoadModelCall(nint objects, nint path, nint subObjectName, nint subObject,
        ulong parameter5, byte useStreaming, uint flags, ulong parameter8, int parameter9);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint LoadMaterialCall(nint materials, nint path, byte makeIfMissing, byte nonRemovable, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateBrushCall(nint translation, nint payload, nint model, nint material);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PlaceholderCall(nint world, nint entity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DephysicalizeCall(nint brush, byte mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DeleteBrushCall(nint brush, uint delete);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReferenceCall(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ByteCall(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidCall(nint instance);
}
