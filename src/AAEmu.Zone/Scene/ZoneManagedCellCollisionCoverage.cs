using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

public sealed record ZoneManagedCellCollisionDiagnostic(string Family, int? SourceOrder, string Message);

/// <summary>A copied observation of one concrete cell generation, never a caller's clearance declaration.</summary>
public sealed class ZoneManagedCellCollisionSnapshot
{
    /// <summary>This proof is for the navigation world's selected physical classes, not every physical interaction.</summary>
    public uint EntitySelectionMask => ZoneManagedCellCollisionCoverage.NavigationEntitySelectionMask;
    internal ZoneManagedCellCollisionCoverage Owner { get; }
    internal ZoneManagedBrushCellCollisionSnapshot? Brushes { get; }
    public ZoneManagedSceneCellLoadReport Input { get; }
    public ulong Generation { get; }
    public ulong PublicationRevision { get; }
    public ulong CharacterGeometryRevision { get; }
    public ulong RayGeometryRevision { get; }
    public ZoneManagedCollisionQueryMask KnownQueryMasks { get; }
    public bool HasMissingInputs { get; }
    public int BrushRows { get; }
    public int TaggedVegetationRows { get; }
    public int SectorVegetationRows { get; }
    public int NonBrushRows { get; }
    public int WaterRows => Water.Count;
    public int EntityRows { get; }
    public int CharacterInstances { get; }
    public int RayInstances { get; }
    public IReadOnlyList<ZoneManagedVegetationPlacementReport> Vegetation { get; }
    public ZoneCellEntityCollisionSnapshot? Entities { get; }
    public IReadOnlyList<ZoneCellNonBrushCollisionCertificate> NonBrush { get; }
    public IReadOnlyList<ZoneCellWaterQueryClassCertificate> Water { get; }
    public IReadOnlyList<ZoneManagedCellCollisionDiagnostic> Diagnostics { get; }

    internal ZoneManagedCellCollisionSnapshot(ZoneManagedCellCollisionCoverage owner,
        ZoneManagedSceneCellLoadReport input, ulong generation, ulong publication, ulong characters, ulong rays,
        ZoneManagedCollisionQueryMask known, bool missing, ZoneManagedBrushCellCollisionSnapshot? brushes,
        IEnumerable<ZoneManagedVegetationPlacementReport> vegetation, ZoneCellEntityCollisionSnapshot? entities,
        IEnumerable<ZoneCellNonBrushCollisionCertificate> nonBrush,
        IEnumerable<ZoneCellWaterQueryClassCertificate> water, int tagged, int sectors,
        int characterInstances, int rayInstances, IEnumerable<ZoneManagedCellCollisionDiagnostic> diagnostics)
    {
        Owner = owner; Input = input; Generation = generation; PublicationRevision = publication;
        CharacterGeometryRevision = characters; RayGeometryRevision = rays; KnownQueryMasks = known;
        HasMissingInputs = missing; Brushes = brushes; BrushRows = brushes?.AuthoredBrushRows ?? 0;
        TaggedVegetationRows = tagged; SectorVegetationRows = sectors;
        Vegetation = Array.AsReadOnly(vegetation.ToArray()); Entities = entities;
        NonBrush = Array.AsReadOnly(nonBrush.ToArray()); Water = Array.AsReadOnly(water.ToArray());
        NonBrushRows = NonBrush.Count + Water.Count;
        EntityRows = entities?.Rows.Count ?? 0; CharacterInstances = characterInstances; RayInstances = rayInstances;
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct().ToArray());
    }

    public bool IsCompleteFor(ZoneManagedCollisionQueryMask mask)
    { ZoneManagedSceneCollision.ValidateQueryMask(mask); return (KnownQueryMasks & mask) == mask; }
}

/// <summary>
/// The dedicated, nonstreaming static scene's physical coverage owner. It consumes every retained
/// cell input, publishes source-admitted vegetation into the existing geometry, and composes
/// concrete brush/entity/road/decal evidence. Unsupported families retain explicit diagnostics.
/// General entity execution, exclusion/road mutation and a streaming-origin change need new owners.
/// </summary>
public sealed class ZoneManagedCellCollisionCoverage : IDisposable
{
    public const uint NavigationEntitySelectionMask = 0x107;
    private readonly ZoneManagedSceneLoadReport _scene;
    private readonly ZoneManagedSceneCollision _collision;
    private readonly ZoneTerrainAssetFiles _files;
    private readonly ZoneManagedSceneCellLoadReport[] _inputs;
    private readonly Dictionary<ZoneManagedSceneCellLoadReport, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ZoneMapCoordinate, Entry[]> _coordinates = [];
    private readonly ZoneManagedCellEntityCollision _entities;
    private readonly ZoneCellNonBrushCollisionContent _nonBrush;
    private readonly ZoneCellWaterQueryClassContent _waterClasses = new();
    private ZoneManagedVegetationResources? _vegetation;
    private string? _vegetationFailure;
    private ulong _revision, _nextGeneration, _observedVegetationRevision;
    private bool _sourceLost;
    public bool IsDisposed { get; private set; }
    public ulong PublicationRevision { get { RefreshRetirement(); return _revision; } }

    public ZoneManagedCellCollisionCoverage(ZoneWorldMapLayout layout, IZoneAssetSource assets,
        ZoneManagedSceneLoadReport scene, ZoneManagedSceneCollision collision,
        ZoneTerrainMaterialResolver materials, ZoneEngineCVars cvars)
    {
        ArgumentNullException.ThrowIfNull(layout); ArgumentNullException.ThrowIfNull(assets);
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _collision = collision ?? throw new ArgumentNullException(nameof(collision));
        ArgumentNullException.ThrowIfNull(materials); ArgumentNullException.ThrowIfNull(cvars);
        if (collision.IsDisposed) throw new ObjectDisposedException(nameof(collision));
        _inputs = scene.Cells.ToArray(); _files = new(assets, layout.World); _entities = new(assets); _nonBrush = new(assets);
        try
        {
            if (_inputs.Any(input => VegetationCount(input) != 0))
                try
                {
                    var catalog = ZoneWorldVegetationContent.Load(assets, layout.World.Header.Name);
                    _vegetation = new(assets, materials, layout, catalog, collision.Geometry, collision.RayGeometry,
                        ZoneManagedVegetationPolicy.FromCVars(cvars), ZoneManagedVegetationExclusions.FreshStaticScene(layout));
                }
                catch (Exception error) when (ContentFailure(error)) { _vegetationFailure = error.Message; }
            foreach (var input in _inputs)
            {
                if (_entries.ContainsKey(input)) continue;
                var entry = new Entry(input, checked(++_nextGeneration)); _entries.Add(input, entry);
                if (!layout.Cells.Any(placement => ReferenceEquals(placement, input.Placement)) ||
                    input.Placement.InvalidLocalX || input.Placement.InvalidLocalY ||
                    (uint)input.Placement.EngineCell.X >= 8 || (uint)input.Placement.EngineCell.Y >= 8)
                { entry.Diagnostics.Add(new("Cell", null, "The input is not a valid exact placement in this scene layout.")); continue; }
                Initialize(entry);
            }
            foreach (var group in _inputs.GroupBy(input => input.Placement.EngineCell))
                _coordinates.Add(group.Key, group.Select(input => _entries[input]).ToArray());
            _observedVegetationRevision = _vegetation?.PublicationRevision ?? 0;
            Advance();
        }
        catch { Dispose(); throw; }
    }

    internal bool Uses(ZoneManagedSceneLoadReport scene, ZoneManagedSceneCollision collision) =>
        ReferenceEquals(scene, _scene) && ReferenceEquals(collision, _collision);

    private void Initialize(Entry entry)
    {
        var input = entry.Input; var cell = input.Placement.EngineCell;
        if (VegetationCount(input) != 0)
        {
            if (_vegetation is null) entry.Diagnostics.Add(new("Vegetation", null,
                _vegetationFailure ?? "The world vegetation publisher is unavailable."));
            else
                try
                {
                    entry.Vegetation = _vegetation.BeginCell(input.Placement);
                    if (input.Objects.Objects is { } objects) entry.Vegetation.LoadTaggedObjects(objects);
                    if (input.Objects.BigObjects is { } big) entry.Vegetation.LoadTaggedObjects(big);
                    if (input.Objects.Vegetation is { } sectors) entry.Vegetation.LoadSectors(sectors);
                }
                catch (Exception error) when (ContentFailure(error)) { entry.Diagnostics.Add(new("Vegetation", null, error.Message)); }
        }
        if (input.Objects.Entities is { } entities)
            try
            {
                entry.Entities = _entities.Classify(entities,
                    _files.GetCellObjectPath(input.Placement.Cell, ZoneCellObjectAsset.Entities));
            }
            catch (Exception error) when (ContentFailure(error)) { entry.Diagnostics.Add(new("Entities", null, error.Message)); }
        foreach (var content in ObjectInputs(input))
            foreach (var row in content.Objects.Where(row => row is not (ZoneCellBrushObject or ZoneCellVegetationObject)))
                try
                {
                    if (row is ZoneCellWaterVolumeObject water) entry.Water.Add(_waterClasses.Classify(content, water));
                    else entry.NonBrush.Add(_nonBrush.Classify(content, row, cell.X, cell.Y));
                }
                catch (Exception error) when (ContentFailure(error)) { entry.Diagnostics.Add(new(content.Kind.ToString(), row.SourceOrder, error.Message)); }
    }

    public ZoneManagedCellCollisionSnapshot CopyCell(ZoneManagedSceneCellLoadReport input)
    {
        ArgumentNullException.ThrowIfNull(input); RefreshRetirement();
        var characters = _collision.Geometry.PublicationRevision; var rays = _collision.RayGeometry.PublicationRevision;
        if (IsDisposed || _sourceLost || !_entries.TryGetValue(input, out var entry) || entry.Retired || entry.Lost)
            return Snapshot(input, entry: null, 0, true, null, [], [], [new("Cell", null, "The exact cell classification publication is absent or retired.")]);
        if (entry.Cached is { } cached && IsCurrent(cached)) return cached;
        var known = ZoneManagedCollisionQueryMask.CharactersAndRays;
        var diagnostics = new List<ZoneManagedCellCollisionDiagnostic>(entry.Diagnostics);
        var missing = false;
        if (!_coordinates.TryGetValue(input.Placement.EngineCell, out var matches) || matches.Length != 1)
        { known = 0; diagnostics.Add(new("Cell", null, "More than one authored input occupies this engine cell.")); }
        foreach (var family in new[] { ZoneCellObjectAsset.Objects, ZoneCellObjectAsset.BigObjects,
            ZoneCellObjectAsset.Vegetation, ZoneCellObjectAsset.Entities })
        {
            var present = family switch
            {
                ZoneCellObjectAsset.Objects => input.Objects.Objects is not null,
                ZoneCellObjectAsset.BigObjects => input.Objects.BigObjects is not null,
                ZoneCellObjectAsset.Vegetation => input.Objects.Vegetation is not null,
                ZoneCellObjectAsset.Entities => input.Objects.Entities is not null,
                _ => false
            };
            if (!present && !input.Objects.Diagnostics.Contains($"{family}: authored asset is absent."))
            { missing = true; known = 0; diagnostics.Add(new(family.ToString(), null, "The required cell inventory is unread, not proven absent.")); }
        }
        var brush = _collision.CopyBrushCell(input); known &= brush.KnownQueryMasks;
        foreach (var diagnostic in brush.Diagnostics) diagnostics.Add(new("Brush", null, diagnostic));
        var placements = entry.Vegetation?.CopyPlacementLeases() ?? [];
        var characterInstances = brush.CharacterInstances; var rayInstances = brush.RayInstances;
        if (VegetationCount(input) != 0)
        {
            if (entry.Vegetation is not { IsCurrent: true } vegetation || !VegetationInputsMatch(input, vegetation) ||
                placements.Count != VegetationCount(input) || !VegetationRowsMatch(input, placements))
            { known = 0; diagnostics.Add(new("Vegetation", null, "The exact vegetation inputs/rows were not completely published once.")); }
            foreach (var placement in placements)
            {
                var report = placement.Report;
                if (!placement.IsCurrent || report.Status == ZoneManagedVegetationPlacementStatus.Unsupported)
                { known = 0; diagnostics.Add(new("Vegetation", report.SourceOrder, report.Diagnostic)); }
                if (placement.CharacterHandles.Length != report.CharacterInstances || placement.RayHandles.Length != report.RayInstances)
                { known = 0; diagnostics.Add(new("Vegetation", report.SourceOrder, "Published handle counts disagree with their exact placement.")); }
                foreach (var handle in placement.CharacterHandles)
                    if (_collision.Geometry.Contains(handle)) characterInstances++;
                    else { known &= ~ZoneManagedCollisionQueryMask.Characters; diagnostics.Add(new("Vegetation", report.SourceOrder, "An owned character proxy retired.")); }
                foreach (var handle in placement.RayHandles)
                    if (_collision.RayGeometry.Contains(handle)) rayInstances++;
                    else { known &= ~ZoneManagedCollisionQueryMask.Rays; diagnostics.Add(new("Vegetation", report.SourceOrder, "An owned ray proxy retired.")); }
            }
        }
        if (input.Objects.Entities is { } entityInput)
        {
            if (entry.Entities is not { } result || !ReferenceEquals(result.Owner, _entities) ||
                !ReferenceEquals(result.Input, entityInput) || result.Rows.Count != (entityInput.Objects?.Children.Count ?? 0) ||
                result.DocumentDiagnostics.Count != 0)
            { known = 0; diagnostics.Add(new("Entities", null, "The exact complete entity document is not classified.")); }
            if (entry.Entities is { } resultRows)
                foreach (var row in resultRows.Rows)
                {
                    known &= (ZoneManagedCollisionQueryMask)row.CertifiedPartFlags;
                    foreach (var diagnostic in row.Diagnostics) diagnostics.Add(new("Entities", row.SourceChildIndex, diagnostic.Message));
                }
        }
        var others = ObjectInputs(input).SelectMany(content => content.Objects
            .Where(row => row is not (ZoneCellBrushObject or ZoneCellVegetationObject or ZoneCellWaterVolumeObject))
            .Select(row => (Content: content, Row: row))).ToArray();
        if (entry.NonBrush.Count != others.Length)
        { known = 0; diagnostics.Add(new("Objects", null, "Not every non-brush/non-vegetation row has a certificate.")); }
        for (var index = 0; index < Math.Min(entry.NonBrush.Count, others.Length); index++)
        {
            var certificate = entry.NonBrush[index]; var expected = others[index];
            if (!ReferenceEquals(certificate.SourceContent, expected.Content) || !ReferenceEquals(certificate.SourceRow, expected.Row) ||
                certificate.EngineCellX != input.Placement.EngineCell.X || certificate.EngineCellY != input.Placement.EngineCell.Y)
            { known = 0; diagnostics.Add(new("Objects", expected.Row.SourceOrder, "The certificate belongs to another exact row or engine cell.")); }
            known &= (ZoneManagedCollisionQueryMask)certificate.CertifiedMask;
            foreach (var diagnostic in certificate.Diagnostics) diagnostics.Add(new(expected.Content.Kind.ToString(), expected.Row.SourceOrder, diagnostic));
        }
        var waters = ObjectInputs(input).SelectMany(content => content.Objects.OfType<ZoneCellWaterVolumeObject>()
            .Select(row => (Content: content, Row: row))).ToArray();
        if (entry.Water.Count != waters.Length)
        { known = 0; diagnostics.Add(new("Water", null, "Not every exact water row has a physical-class bound.")); }
        for (var index = 0; index < Math.Min(entry.Water.Count, waters.Length); index++)
        {
            var certificate = entry.Water[index]; var expected = waters[index];
            if (!ReferenceEquals(certificate.SourceContent, expected.Content) || !ReferenceEquals(certificate.SourceRow, expected.Row) ||
                !certificate.HasClassUpperBound || certificate.CanContributeTo(NavigationEntitySelectionMask))
            { known = 0; diagnostics.Add(new("Water", expected.Row.SourceOrder, "The exact water row is not excluded by the navigation entity-selection mask.")); }
            foreach (var diagnostic in certificate.Diagnostics) diagnostics.Add(new("Water", expected.Row.SourceOrder, diagnostic));
        }
        if (entry.Diagnostics.Count != 0) known = 0;
        return entry.Cached = Snapshot(input, entry, known, missing, brush, placements.Select(placement => placement.Report),
            entry.NonBrush, diagnostics, characterInstances, rayInstances);

        ZoneManagedCellCollisionSnapshot Snapshot(ZoneManagedSceneCellLoadReport source, Entry? entry,
            ZoneManagedCollisionQueryMask masks, bool missingInputs, ZoneManagedBrushCellCollisionSnapshot? brushes,
            IEnumerable<ZoneManagedVegetationPlacementReport> vegetation,
            IEnumerable<ZoneCellNonBrushCollisionCertificate> other, IEnumerable<ZoneManagedCellCollisionDiagnostic> issues,
            int characterCount = 0, int rayCount = 0) => new(this, source, entry?.Generation ?? 0, _revision, characters, rays,
                masks, missingInputs, brushes, vegetation, entry?.Entities, other, entry?.Water ?? [],
                TaggedCount(source), source.Objects.Vegetation?.Instances.Count ?? 0, characterCount, rayCount, issues);
    }

    public IReadOnlyList<ZoneManagedCellCollisionSnapshot> CopyCells() =>
        Array.AsReadOnly(_inputs.Select(CopyCell).ToArray());

    public bool IsCurrent(ZoneManagedCellCollisionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot); RefreshRetirement();
        return !IsDisposed && !_sourceLost && ReferenceEquals(snapshot.Owner, this) &&
            _entries.TryGetValue(snapshot.Input, out var entry) && !entry.Retired && !entry.Lost &&
            entry.Generation == snapshot.Generation && snapshot.PublicationRevision == _revision &&
            snapshot.CharacterGeometryRevision == _collision.Geometry.PublicationRevision &&
            snapshot.RayGeometryRevision == _collision.RayGeometry.PublicationRevision &&
            snapshot.Brushes is { } brush && _collision.IsCurrent(brush);
    }

    public ZoneNavigationSegmentAdmission Check(ZonePhysicsQueryBounds bounds, ZoneManagedCollisionQueryMask mask)
    {
        ZoneManagedSceneCollision.ValidateQueryMask(mask);
        if (!Finite(bounds.Minimum) || !Finite(bounds.Maximum) || Vector3.Min(bounds.Minimum, bounds.Maximum) != bounds.Minimum)
            throw new ArgumentException("Collision coverage requires finite ordered query bounds.", nameof(bounds));
        if (bounds.Minimum.X < 0 || bounds.Minimum.Y < 0 || bounds.Maximum.X >= 8192 || bounds.Maximum.Y >= 8192)
            return new(ZoneNavigationSegmentOutcome.MissingData, "The query leaves the loaded engine-local cell window.");
        for (var x = (int)(bounds.Minimum.X / 1024); x <= (int)(bounds.Maximum.X / 1024); x++)
            for (var y = (int)(bounds.Minimum.Y / 1024); y <= (int)(bounds.Maximum.Y / 1024); y++)
            {
                if (!_coordinates.TryGetValue(new(x, y), out var cells) || cells.Length != 1)
                    return new(ZoneNavigationSegmentOutcome.MissingData, $"The query requires one exact current collision inventory in cell({x},{y}).");
                var snapshot = CopyCell(cells[0].Input);
                if (!snapshot.IsCompleteFor(mask) || !IsCurrent(snapshot))
                    return new(snapshot.HasMissingInputs ? ZoneNavigationSegmentOutcome.MissingData : ZoneNavigationSegmentOutcome.Unsupported,
                        $"Cell({x},{y}) collision is unadmitted for {mask}: " + string.Join("; ", snapshot.Diagnostics.Take(4).Select(item => item.Family + ": " + item.Message)));
            }
        //Native physical queries walk the world's actual entity/thunk grid; they
        //do not filter by the authored cell that created a body. An unknown source
        //in a different loaded cell may cross this query. Until a concrete owner
        //proves its selected-class/part exclusion or authoritative current physical
        //influence bound, source-cell distance/render bounds cannot certify absence.
        foreach (var input in _inputs)
        {
            var snapshot = CopyCell(input);
            if (!snapshot.IsCompleteFor(mask) || !IsCurrent(snapshot))
                return new(snapshot.HasMissingInputs ? ZoneNavigationSegmentOutcome.MissingData : ZoneNavigationSegmentOutcome.Unsupported,
                    $"Loaded cell({input.Placement.EngineCell.X},{input.Placement.EngineCell.Y}) has unclassified possible world-query contributions: " +
                    string.Join("; ", snapshot.Diagnostics.Take(4).Select(item => item.Family + ": " + item.Message)));
        }
        return new(ZoneNavigationSegmentOutcome.Clear);
    }

    public bool RetireCell(ZoneManagedSceneCellLoadReport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (IsDisposed || !_entries.TryGetValue(input, out var entry) || entry.Retired) return false;
        Advance(); entry.Retired = true; entry.Cached = null;
        entry.Vegetation?.Dispose(); _collision.RetireBrushCell(input); return true;
    }

    private void RefreshRetirement()
    {
        if (IsDisposed) return;
        if ((_vegetation?.PublicationRevision ?? 0) != _observedVegetationRevision)
        { Advance(); _observedVegetationRevision = _vegetation?.PublicationRevision ?? 0; }
        if (!_sourceLost && (_collision.IsDisposed || _entities.IsDisposed || _nonBrush.IsDisposed || _waterClasses.IsDisposed ||
            _vegetation?.IsDisposed == true || _scene.Cells.Count != _inputs.Length ||
            _inputs.Where((input, index) => !ReferenceEquals(input, _scene.Cells[index])).Any()))
        { Advance(); _sourceLost = true; }
        foreach (var entry in _entries.Values)
            if (!entry.Retired && !entry.Lost && entry.Vegetation is { } vegetation &&
                !vegetation.IsCurrent)
            { Advance(); entry.Lost = true; entry.Cached = null; }
    }
    private void Advance()
    {
        if (_revision == ulong.MaxValue) throw new InvalidOperationException("Cell collision publication identities are exhausted.");
        _revision++;
    }
    private static bool VegetationInputsMatch(ZoneManagedSceneCellLoadReport input, ZoneManagedVegetationCellLease vegetation)
    {
        var expected = ObjectInputs(input).ToArray(); var tagged = vegetation.CopyTaggedInputs();
        if (tagged.Count != expected.Length || expected.Any(content =>
            tagged.Count(item => ReferenceEquals(item.Content, content) && item.Completed) != 1)) return false;
        return input.Objects.Vegetation is { } sectors
            ? vegetation.SectorInput is { Completed: true } read && ReferenceEquals(read.Content, sectors)
            : vegetation.SectorInput is null;
    }
    private static bool VegetationRowsMatch(ZoneManagedSceneCellLoadReport input,
        IReadOnlyList<ZoneManagedVegetationPlacementLease> placements)
    {
        var expected = ObjectInputs(input).SelectMany(content => content.Objects.OfType<ZoneCellVegetationObject>()
            .Select(row => (Source: content.Kind.ToString(), Order: row.SourceOrder, Offset: row.SourceOffset, Group: row.VegetationGroupIndex)))
            .Concat((input.Objects.Vegetation?.Sectors ?? []).SelectMany(sector => sector.Instances.Select(row =>
                (Source: $"vegetation.dat/sector{sector.SourceIndex}", Order: row.SourceOrder, Offset: row.SourceOffset, Group: row.VegetationGroupIndex)))).ToArray();
        return expected.Length == placements.Count && expected.Select((row, index) =>
            row == (placements[index].Report.Source, placements[index].Report.SourceOrder,
                placements[index].Report.SourceOffset, placements[index].Report.GroupId)).All(match => match);
    }
    private static IEnumerable<ZoneCellObjectContent> ObjectInputs(ZoneManagedSceneCellLoadReport input)
    {
        if (input.Objects.Objects is { } objects) yield return objects;
        if (input.Objects.BigObjects is { } big) yield return big;
    }
    private static int TaggedCount(ZoneManagedSceneCellLoadReport input) => ObjectInputs(input)
        .Sum(content => content.Objects.OfType<ZoneCellVegetationObject>().Count());
    private static int VegetationCount(ZoneManagedSceneCellLoadReport input) =>
        TaggedCount(input) + (input.Objects.Vegetation?.Instances.Count ?? 0);
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static bool ContentFailure(Exception error) => error is IOException or InvalidDataException or ArgumentException or NotSupportedException;
    public void Dispose()
    {
        if (IsDisposed) return; Advance(); IsDisposed = true;
        foreach (var entry in _entries.Values) { entry.Retired = true; entry.Vegetation?.Dispose(); }
        _vegetation?.Dispose(); _entities.Dispose(); _nonBrush.Dispose(); _waterClasses.Dispose();
    }
    private sealed class Entry(ZoneManagedSceneCellLoadReport input, ulong generation)
    {
        public ZoneManagedSceneCellLoadReport Input { get; } = input;
        public ulong Generation { get; } = generation;
        public bool Retired, Lost;
        public ZoneManagedVegetationCellLease? Vegetation;
        public ZoneCellEntityCollisionSnapshot? Entities;
        public List<ZoneCellNonBrushCollisionCertificate> NonBrush { get; } = [];
        public List<ZoneCellWaterQueryClassCertificate> Water { get; } = [];
        public List<ZoneManagedCellCollisionDiagnostic> Diagnostics { get; } = [];
        public ZoneManagedCellCollisionSnapshot? Cached;
    }
}
