using System.Numerics;

using AAEmu.Zone.Content;
using AAEmu.Zone.Native.StaticObjects;

namespace AAEmu.Zone.Scene;

/// <summary>One authored vegetation attempt, including native configuration/group skips.</summary>
public sealed class ZoneCellVegetationResource
{
    public int SourceOrder { get; }
    public int SourceOffset { get; }
    public short GroupId { get; }
    public Vector3 Translation { get; }
    public ZoneNativeStaticObjectServices.Vegetation? Resource { get; internal set; }

    internal ZoneCellVegetationResource(int order, int offset, short groupId, Vector3 translation)
    { SourceOrder = order; SourceOffset = offset; GroupId = groupId; Translation = translation; }
}

/// <summary>
/// Owns one cell's vegetation instances while borrowing the native world group catalog. Sector
/// loading follows the sixteen authored descriptors and pumps native completion after each
/// nonempty sector. Tagged type-two records can be appended by a mixed-object dispatcher in
/// source order, interleaved with other families; this owner does not reorder a complete object file.
/// Neither this collection nor a native skip establishes complete cell collision readiness.
/// Retire it before its world group catalog and static-object service.
/// </summary>
public sealed class ZoneCellVegetationResources : IDisposable
{
    private readonly ZoneNativeStaticObjectServices _services;
    private readonly List<ZoneCellVegetationResource> _instances = [];
    private readonly int _thread;
    private bool _disposed;
    private bool _failed;
    private bool _operating;
    private bool _retiring;

    public ZoneWorldMapCellPlacement Placement { get; }
    public ZoneNativeStaticObjectServices.VegetationGroups Groups { get; }
    public IReadOnlyList<ZoneCellVegetationResource> Instances { get; }
    public ZoneCellVegetationContent? SectorContent { get; private set; }

    public ZoneCellVegetationResources(ZoneNativeStaticObjectServices services,
        ZoneNativeStaticObjectServices.VegetationGroups groups, ZoneWorldMapCellPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(placement);
        if (!ReferenceEquals(groups.Owner, services) || groups.IsDisposed)
            throw new ArgumentException("Cell vegetation requires this service's live world group catalog.", nameof(groups));
        _thread = groups.OwnerThread;
        VerifyAccess();
        if (!ReferenceEquals(groups.World.GetCell(placement.Cell.Coordinate.X, placement.Cell.Coordinate.Y), placement.Cell))
            throw new ArgumentException("The cell and vegetation groups belong to different worlds.", nameof(placement));
        if ((uint)placement.EngineCell.X >= 8 || (uint)placement.EngineCell.Y >= 8)
            throw new ArgumentOutOfRangeException(nameof(placement), "Vegetation requires a resolved cell in the native 8 by 8 window.");
        _services = services;
        Groups = groups;
        Placement = placement;
        Instances = _instances.AsReadOnly();
    }

    /// <summary>
    /// Appends one main/big-file type-two record at its native cell translation. Native30225050
    /// does not apply the brush-only visibility-area admission gate to vegetation.
    /// The enclosing dispatcher must preserve order across all object families and retain inputs.
    /// </summary>
    public ZoneCellVegetationResource AddObject(ZoneCellVegetationObject authored)
    {
        ArgumentNullException.ThrowIfNull(authored);
        CheckAvailable();
        _operating = true;
        try
        {
            var translation = new Vector3(Placement.EngineCell.X << 10, Placement.EngineCell.Y << 10, 0);
            return Add(authored.SourceOrder, authored.SourceOffset, authored.VegetationGroupIndex,
                translation, authored.RawPayload);
        }
        finally { _operating = false; }
    }

    /// <summary>
    /// Loads the sector file once after preceding main objects. A failed attempt retains all
    /// partial native ownership and cannot be replayed; dispose before trying a new cell owner.
    /// </summary>
    public void LoadSectors(ZoneCellVegetationContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        CheckAvailable();
        if (SectorContent is not null)
            throw new InvalidOperationException("The cell vegetation sectors were already attempted.");
        _instances.EnsureCapacity(checked(_instances.Count + content.Instances.Count));
        SectorContent = content;
        _operating = true;
        try
        {
            foreach (var sector in content.Sectors)
            {
                var translation = new Vector3((Placement.EngineCell.X * 4 + sector.LocalX) << 8,
                    (Placement.EngineCell.Y * 4 + sector.LocalY) << 8, 0);
                foreach (var authored in sector.Instances)
                    Add(authored.SourceOrder, authored.SourceOffset, authored.VegetationGroupIndex,
                        translation, authored.RawPayload);
                if (sector.ByteLength != 0) _services.CompleteVegetationSector(Groups);
            }
        }
        catch { _failed = true; throw; }
        finally { _operating = false; }
    }

    private ZoneCellVegetationResource Add(int order, int offset, short groupId,
        Vector3 translation, ReadOnlySpan<byte> payload)
    {
        var instance = new ZoneCellVegetationResource(order, offset, groupId, translation);
        _instances.Add(instance); // Root managed ownership before the native factory can publish.
        try
        {
            instance.Resource = _services.CreateVegetation(Groups, translation, payload);
            if (instance.Resource is { } resource) _services.CompleteCellVegetation(resource);
            return instance;
        }
        catch { _failed = true; throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess();
        if (_operating) throw new InvalidOperationException("Cell vegetation cannot retire during a native resource operation.");
        _retiring = true;
        _operating = true;
        // Replacement rollback order. Stop at uncertain native cleanup and retain the remaining
        // cell resources, shared group owner and service; do not release their dependencies early.
        var diagnosticRemaining = 0; var diagnosticAttempts = 0; var diagnosticRetired = 0;
        Exception? diagnosticError = null;
        try
        {
            for (var i = _instances.Count - 1; i >= 0; i--)
            {
                diagnosticRemaining = i;
                diagnosticAttempts++;
                if (_instances[i].Resource is not { } resource) { diagnosticRetired++; continue; }
                resource.Dispose();
                diagnosticRetired++;
            }
            _disposed = true;
        }
        catch (Exception error) { diagnosticError = error; throw; }
        finally
        {
            // The diagnostic type is nested in ZoneNativeStaticObjectServices, so this sibling
            // top-level class has to qualify it.
            ZoneNativeStaticObjectServices.RetirementDiagnostic.VegetationCellDispose(this, diagnosticRemaining, diagnosticAttempts,
                diagnosticRetired, diagnosticError); // TEMPORARY retirement-diagnostic instrumentation.
            _operating = false;
        }
    }

    private void CheckAvailable()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed || _retiring, this);
        ObjectDisposedException.ThrowIf(Groups.IsDisposed, Groups);
        if (_operating) throw new InvalidOperationException("Cell vegetation operations cannot reenter their owner.");
        if (_failed) throw new InvalidOperationException("Retire the failed vegetation attempt before loading more cell resources.");
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Cell vegetation must run on its native group's owning thread.");
    }
}
