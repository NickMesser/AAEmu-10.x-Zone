using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>Native part/query categories, separately from physical entity-selection masks.</summary>
[Flags]
public enum ZoneManagedCollisionQueryMask : uint
{
    Characters = 0x2,
    Rays = 0x8000,
    CharactersAndRays = Characters | Rays
}

/// <summary>A copied result from this collision owner's actual brush publication.</summary>
public sealed class ZoneManagedBrushCellCollisionSnapshot
{
    internal ZoneManagedSceneCollision Owner { get; }
    public ZoneManagedSceneCellLoadReport Input { get; }
    public ulong Generation { get; }
    public ulong PublicationRevision { get; }
    public ulong CharacterGeometryRevision { get; }
    public ulong RayGeometryRevision { get; }
    public int AuthoredBrushRows { get; }
    public int ClassifiedBrushRows { get; }
    public int CharacterInstances { get; }
    public int RayInstances { get; }
    public ZoneManagedCollisionQueryMask KnownQueryMasks { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    internal ZoneManagedBrushCellCollisionSnapshot(ZoneManagedSceneCollision owner,
        ZoneManagedSceneCellLoadReport input, ulong generation, ulong publication,
        ulong characters, ulong rays, int authored, int classified, int characterInstances,
        int rayInstances, ZoneManagedCollisionQueryMask known, IEnumerable<string> diagnostics)
    {
        Owner = owner; Input = input; Generation = generation; PublicationRevision = publication;
        CharacterGeometryRevision = characters; RayGeometryRevision = rays;
        AuthoredBrushRows = authored; ClassifiedBrushRows = classified;
        CharacterInstances = characterInstances; RayInstances = rayInstances;
        KnownQueryMasks = known; Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public bool IsCompleteFor(ZoneManagedCollisionQueryMask mask)
    {
        ZoneManagedSceneCollision.ValidateQueryMask(mask);
        return (KnownQueryMasks & mask) == mask;
    }
}

public sealed partial class ZoneManagedSceneCollision
{
    private readonly Dictionary<ZoneManagedSceneCellLoadReport, BrushCellPublication> _brushCells =
        new(ReferenceEqualityComparer.Instance);
    private ulong _nextBrushCellGeneration, _brushPublicationRevision;

    /// <summary>Includes bodyless cell retirement, which cannot change a geometry revision.</summary>
    public ulong BrushPublicationRevision => _brushPublicationRevision;

    private BrushCellPublication TrackBrushCell(ZoneManagedSceneCellLoadReport input)
    {
        if (_brushCells.TryGetValue(input, out var existing)) return existing;
        var cell = new BrushCellPublication(input, checked(++_nextBrushCellGeneration));
        AdvanceBrushPublication(); _brushCells.Add(input, cell); return cell;
    }

    private static void TrackBrushPlacement(BrushCellPublication cell, ZoneCellObjectContent input,
        ZoneCellBrushObject row, IEnumerable<ZoneManagedStaticInstanceHandle> characters,
        IEnumerable<ZoneManagedStaticInstanceHandle> rays, bool complete, string? diagnostic)
    {
        cell.Rows.Add(new(input, row, characters.ToArray(), rays.ToArray(), complete, diagnostic));
        cell.Cached = null;
    }

    public ZoneManagedBrushCellCollisionSnapshot CopyBrushCell(ZoneManagedSceneCellLoadReport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var characterRevision = Geometry.PublicationRevision; var rayRevision = RayGeometry.PublicationRevision;
        if (_disposed || !_brushCells.TryGetValue(input, out var cell) || cell.Retired)
            return new(this, input, 0, _brushPublicationRevision, characterRevision, rayRevision,
                CountBrushes(input), 0, 0, 0, 0, ["The exact brush cell publication is absent or retired."]);
        if (cell.Cached is { } cached && cached.PublicationRevision == _brushPublicationRevision &&
            cached.CharacterGeometryRevision == characterRevision && cached.RayGeometryRevision == rayRevision)
            return cached;
        var diagnostics = new List<string>();
        var known = ZoneManagedCollisionQueryMask.CharactersAndRays;
        var expected = CountBrushes(input);
        if (input.Objects.Objects is null && !input.Objects.Diagnostics.Contains("Objects: authored asset is absent.") ||
            input.Objects.BigObjects is null && !input.Objects.Diagnostics.Contains("BigObjects: authored asset is absent."))
        { diagnostics.Add("A brush input is unread rather than a proven absent authored file."); known = 0; }
        if (cell.Rows.Count != expected || cell.Rows.Any(row =>
            !ReferenceEquals(row.Input, input.Objects.Objects) && !ReferenceEquals(row.Input, input.Objects.BigObjects)) ||
            cell.Rows.Select(row => row.Row).Distinct(ReferenceEqualityComparer.Instance).Count() != expected)
        {
            diagnostics.Add("The brush publication does not account for every exact input row once."); known = 0;
        }
        if (input.Objects.Objects is { Kind: not ZoneCellObjectFileKind.Octree } ||
            input.Objects.BigObjects is { Kind: not ZoneCellObjectFileKind.BigObjects })
        { diagnostics.Add("The brush input kind disagrees with its cell file identity."); known = 0; }
        var classified = 0; var characterCount = 0; var rayCount = 0;
        foreach (var row in cell.Rows)
        {
            if (row.Complete) classified++;
            else { known = 0; diagnostics.Add(row.Diagnostic ?? "A brush has an unowned resource or physical placement."); }
            foreach (var handle in row.Characters)
                if (Geometry.Contains(handle)) characterCount++;
                else { known &= ~ZoneManagedCollisionQueryMask.Characters; diagnostics.Add("An owned brush character proxy has retired."); }
            foreach (var handle in row.Rays)
                if (RayGeometry.Contains(handle)) rayCount++;
                else { known &= ~ZoneManagedCollisionQueryMask.Rays; diagnostics.Add("An owned brush ray proxy has retired."); }
        }
        return cell.Cached = new(this, input, cell.Generation, _brushPublicationRevision,
            characterRevision, rayRevision, expected, classified, characterCount, rayCount, known,
            diagnostics.Distinct(StringComparer.Ordinal));
    }

    public bool IsCurrent(ZoneManagedBrushCellCollisionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !_disposed && ReferenceEquals(snapshot.Owner, this) &&
            _brushCells.TryGetValue(snapshot.Input, out var cell) && !cell.Retired &&
            cell.Generation == snapshot.Generation && snapshot.PublicationRevision == _brushPublicationRevision &&
            snapshot.CharacterGeometryRevision == Geometry.PublicationRevision &&
            snapshot.RayGeometryRevision == RayGeometry.PublicationRevision;
    }

    /// <summary>Invalidates this exact input before removing only its published brush proxies.</summary>
    public bool RetireBrushCell(ZoneManagedSceneCellLoadReport input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_disposed || !_brushCells.TryGetValue(input, out var cell) || cell.Retired) return false;
        AdvanceBrushPublication(); cell.Retired = true; cell.Cached = null;
        foreach (var row in cell.Rows)
        {
            foreach (var handle in row.Characters) Geometry.RemoveInstance(handle);
            foreach (var handle in row.Rays) RayGeometry.RemoveInstance(handle);
        }
        return true;
    }

    private void RetireAllBrushCells()
    {
        if (_brushCells.Values.Any(cell => !cell.Retired)) AdvanceBrushPublication();
        foreach (var cell in _brushCells.Values) { cell.Retired = true; cell.Cached = null; }
    }
    private void AdvanceBrushPublication()
    {
        if (_brushPublicationRevision == ulong.MaxValue)
            throw new InvalidOperationException("Brush cell publication identities are exhausted.");
        _brushPublicationRevision++;
    }
    private static int CountBrushes(ZoneManagedSceneCellLoadReport input) =>
        (input.Objects.Objects?.Objects.OfType<ZoneCellBrushObject>().Count() ?? 0) +
        (input.Objects.BigObjects?.Objects.OfType<ZoneCellBrushObject>().Count() ?? 0);
    internal static void ValidateQueryMask(ZoneManagedCollisionQueryMask mask)
    {
        if (mask == 0 || (mask & ~ZoneManagedCollisionQueryMask.CharactersAndRays) != 0)
            throw new ArgumentOutOfRangeException(nameof(mask), "Only character/ray part categories are owned by this collision profile.");
    }
    private sealed class BrushCellPublication(ZoneManagedSceneCellLoadReport input, ulong generation)
    {
        public ZoneManagedSceneCellLoadReport Input { get; } = input;
        public ulong Generation { get; } = generation;
        public List<BrushPlacementPublication> Rows { get; } = [];
        public bool Retired;
        public ZoneManagedBrushCellCollisionSnapshot? Cached;
    }
    private sealed record BrushPlacementPublication(ZoneCellObjectContent Input, ZoneCellBrushObject Row,
        ZoneManagedStaticInstanceHandle[] Characters, ZoneManagedStaticInstanceHandle[] Rays, bool Complete, string? Diagnostic);
}
