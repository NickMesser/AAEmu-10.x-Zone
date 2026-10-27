using AAEmu.Zone.Content;
using AAEmu.Zone.Native.StaticObjects;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Owns the temporary native model/material references shared by one authored object file.
/// Both table order and empty slots are preserved. All families borrow the same table owner
/// during ordered publication, then release it: native instances retain their own dependencies.
/// A successfully loaded table does not establish object, collision or complete cell readiness.
/// </summary>
public sealed class ZoneCellObjectTables : IDisposable
{
    private readonly ZoneNativeStaticObjectServices _services;
    private readonly ZoneNativeStaticObjectServices.Model?[] _models;
    private readonly ZoneNativeStaticObjectServices.Material?[] _materials;
    private readonly int _thread;
    private bool _operating, _retiring, _disposed;

    public ZoneCellObjectContent Content { get; }
    public IReadOnlyList<ZoneNativeStaticObjectServices.Model?> Models { get; }
    public IReadOnlyList<ZoneNativeStaticObjectServices.Material?> Materials { get; }
    public bool IsDisposed => _disposed;

    private ZoneCellObjectTables(ZoneNativeStaticObjectServices services, ZoneCellObjectContent content)
    {
        _services = services;
        _thread = Environment.CurrentManagedThreadId;
        Content = content;
        _models = new ZoneNativeStaticObjectServices.Model?[content.ModelResources.Count];
        _materials = new ZoneNativeStaticObjectServices.Material?[content.MaterialResources.Count];
        Models = Array.AsReadOnly(_models);
        Materials = Array.AsReadOnly(_materials);
    }

    public static ZoneCellObjectTables Create(ZoneNativeStaticObjectServices services, ZoneCellObjectContent content)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(content);
        services.VerifyResourceOwner();
        var owner = new ZoneCellObjectTables(services, content);
        try
        {
            owner.Load();
            return owner;
        }
        catch (Exception error)
        {
            // The static service also roots every native reference before acquisition, including
            // a partially completed factory whose table assignment has not returned yet.
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private void Load()
    {
        _operating = true;
        try
        {
            foreach (var resource in Content.ModelResources)
                if (resource.Name.Length != 0) _models[resource.Index] = _services.LoadModel(resource.Name);
            foreach (var resource in Content.MaterialResources)
                if (resource.Name.Length != 0) _materials[resource.Index] = _services.LoadMaterial(resource.Name);
        }
        finally { _operating = false; }
    }

    internal void ValidateFor(ZoneNativeStaticObjectServices services, ZoneCellObjectContent content)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed || _retiring, this);
        if (_operating) throw new InvalidOperationException("Cell resource tables cannot reenter an active operation.");
        if (!ReferenceEquals(services, _services) || !ReferenceEquals(content, Content))
            throw new ArgumentException("Cell resources require their exact file's tables from the same native service.");
        _services.VerifyResourceOwner();
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyThread();
        if (_operating) throw new InvalidOperationException("Cell resource tables cannot retire during an active operation.");
        _retiring = true;
        _operating = true;
        try
        {
            // Both 30166870 and 30163530 release the material vector before the model vector,
            // in ascending table order. Stop on uncertain cleanup and retain remaining references.
            for (var i = 0; i < _materials.Length; i++)
            {
                _materials[i]?.Dispose();
                _materials[i] = null;
            }
            for (var i = 0; i < _models.Length; i++)
            {
                _models[i]?.Dispose();
                _models[i] = null;
            }
            _disposed = true;
        }
        finally { _operating = false; }
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Cell resource tables require their native service's owning thread.");
    }
}
