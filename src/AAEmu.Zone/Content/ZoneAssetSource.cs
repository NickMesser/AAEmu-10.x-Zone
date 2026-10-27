using AAEmu.Commons.Utils.AAPak;
using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>
/// Opens a UTF-8 archive-relative asset (for example game/worlds/main_world/surface.xml).
/// Streams belong to the caller. Null means absent; malformed sources and read failures throw.
/// Native resource-name normalization belongs to the resource loader, before this boundary.
/// </summary>
public interface IZoneAssetSource
{
    Stream? OpenRead(string virtualPath);
}

/// <summary>A read-only loose asset tree, rooted at the directory containing game/.</summary>
public sealed class ZoneDirectoryAssetSource : IZoneAssetSource
{
    private readonly string _root;

    public ZoneDirectoryAssetSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
    }

    public Stream? OpenRead(string virtualPath)
    {
        var relative = ZoneAssetPath.Validate(virtualPath).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(_root, relative);
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

/// <summary>
/// An immutable index from an explicitly read-only AAPak open. Assets use position-independent
/// reads on that exact archive, so parallel loads cannot move another reader's cursor.
/// This source never extracts assets or opens the archive for writing.
/// </summary>
public sealed class ZonePakAssetSource : IZoneAssetSource, IDisposable
{
    private sealed record Entry(long Offset, long Length);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AAPak _archiveOwner;
    private readonly FileStream _anchor;
    private readonly long _archiveLength;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _readers;
    public int Count => _entries.Count;

    public ZonePakAssetSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var pak = new AAPak("");
        try
        {
            if (!pak.OpenPak(_path, openAsReadOnly: true) || !pak.isOpen || pak.isVirtual || !pak.readOnly)
                throw new InvalidDataException($"Cannot read the game archive: {_path}");
            // Retain the parser's exact handle, including across Unix path replacement.
            _archiveOwner = pak;
            _anchor = pak._gpFileStream;
            _archiveLength = _anchor.Length;
            // Widen before addition: the shared parser's uint sum can wrap on malformed footers.
            var declaredCount = (long)pak._header.fileCount + pak._header.extraFileCount;
            var dataEnd = pak._header.FirstFileInfoOffset;
            if (_archiveLength < 512 || declaredCount > (_archiveLength - 512) / 0x150 ||
                pak._header.fileCount != pak.pakFiles.Count || pak._header.extraFileCount != pak.extraFiles.Count ||
                dataEnd < 0 || dataEnd > _archiveLength - 512)
                throw new InvalidDataException("The game archive has an inconsistent file table.");
            foreach (var (name, file) in pak.pakFiles)
            {
                if (file.offset < 0 || file.size < 0 || file.offset > dataEnd || file.size > dataEnd - file.offset)
                    throw new InvalidDataException($"Archive asset {name} exceeds the archive data bounds.");
                _entries.Add(name, new(file.offset, file.size));
            }
            pak.pakFiles.Clear();
            pak.extraFiles.Clear();
        }
        catch
        {
            // OpenPak may leave a handle behind when header parsing fails before isOpen is set.
            pak._gpFileStream?.Dispose();
            pak.ClosePak();
            throw;
        }
        finally { pak._header.FAT.Dispose(); }
    }

    public Stream? OpenRead(string virtualPath)
    {
        // AAPak's index reader retains each filename byte as one Latin-1 char.
        var name = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(ZoneAssetPath.Validate(virtualPath)));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(name, out var entry)) return null;
            var stream = new OwnedAssetStream(this, entry.Offset, entry.Length);
            _readers++;
            return stream;
        }
    }

    /// <summary>Stops new asset opens. Existing streams retain the indexed handle until their disposal.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_readers == 0) _archiveOwner.ClosePak();
        }
    }

    private void ReleaseReader()
    {
        lock (_gate)
        {
            _readers--;
            if (_disposed && _readers == 0) _archiveOwner.ClosePak();
        }
    }

    private sealed class OwnedAssetStream : Stream
    {
        private readonly ZonePakAssetSource _owner;
        private readonly long _offset;
        private readonly long _length;
        private long _position;
        private int _disposed;

        internal OwnedAssetStream(ZonePakAssetSource owner, long offset, long length)
        { _owner = owner; _offset = offset; _length = length; }

        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => CanRead;
        public override bool CanWrite => false;
        public override long Length { get { CheckDisposed(); return _length; } }
        public override long Position
        {
            get { CheckDisposed(); return _position; }
            set { CheckDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = Math.Min(value, _length); }
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            CheckDisposed();
            var count = (int)Math.Min(buffer.Length, _length - _position);
            if (count == 0) return 0;
            var read = RandomAccess.Read(_owner._anchor.SafeFileHandle, buffer[..count], _offset + _position);
            if (read == 0) throw new EndOfStreamException("The indexed archive asset was truncated.");
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }
        public override void Flush() => CheckDisposed();
        public override void SetLength(long value) => throw new NotSupportedException("Zone assets are read-only.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Zone assets are read-only.");
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _owner.ReleaseReader();
            base.Dispose(disposing);
        }
        private void CheckDisposed() => ObjectDisposedException.ThrowIf(!CanRead, this);
    }
}

/// <summary>Caller-selected source priority. Existing empty assets stop the search. Sources remain caller-owned.</summary>
public sealed class ZoneAssetSourceStack : IZoneAssetSource
{
    private readonly IZoneAssetSource[] _sources;
    public ZoneAssetSourceStack(params IZoneAssetSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(source => source is null)) throw new ArgumentException("Asset sources cannot contain null.", nameof(sources));
        _sources = sources.ToArray();
    }

    public Stream? OpenRead(string virtualPath)
    {
        var path = ZoneAssetPath.Validate(virtualPath);
        foreach (var source in _sources)
        {
            var stream = source.OpenRead(path);
            if (stream is not null) return stream;
        }
        return null;
    }
}

internal static class ZoneAssetPath
{
    internal static string Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Replace('\\', '/');
        if (path[0] == '/' || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Zone assets require a relative path without empty, dot or parent segments.", nameof(path));
        return path;
    }
}
