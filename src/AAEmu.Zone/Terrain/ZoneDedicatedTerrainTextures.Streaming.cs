using System.Numerics;

using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Terrain;

public enum ZoneTerrainNodeTextureState { Idle, Reading, Complete }

public readonly record struct ZoneTerrainTextureTransform(Vector2 Offset, float Scale,
    float MinimumHeight, float MaximumHeight, ZoneTerrainNodeBounds Bounds);

public sealed record ZoneTerrainNodeTextureSnapshot(ZoneTerrainNodeTextureState State,
    short BaseHandle, short AmbientOcclusionHandle, ZoneTerrainTextureTransform BaseTransform,
    ZoneTerrainTextureTransform AmbientOcclusionTransform, uint? LastReadError,
    IReadOnlyList<string> Diagnostics);

public readonly record struct ZoneTerrainNodeTextureRange(uint Offset, int Length);

public sealed partial class ZoneDedicatedTerrainTextures
{
    private readonly string?[] _cellPaths = new string?[64];
    private readonly Dictionary<ZoneTerrainNode, NodeTexture> _nodeTextures = [];
    private ZoneNativeTerrainStreams? _streams;
    private int? _streamThread;
    private NodeRead? _activeRead;
    private Exception? _streamFault;

    // 300E6FD0 clears both complete transform blocks and sets state/override to zero after
    // construction. Constructor-only scale=1 is superseded by this tree creation producer.
    private sealed class NodeTexture
    {
        internal ZoneTerrainNodeTextureState State;
        internal short BaseHandle, AoHandle;
        internal ZoneTerrainTextureTransform BaseTransform, AoTransform;
        internal uint? LastReadError;
        internal readonly List<string> Diagnostics = [];
    }

    private sealed record NodeRead(ZoneTerrainNode Node, NodeTexture Texture,
        ZoneTerrainTextureContent Content, ZoneTerrainNodeTextureRange Range,
        ZoneNativeTerrainStreams.ReadRequest Request);

    /// <summary>
    /// Connects this terrain owner to the actual CSystem stream service on its simulation thread.
    /// The connection belongs to this object and retires before its metadata/pools and the engine.
    /// No terrain scheduling, native renderer substitution or scene readiness is implied.
    /// </summary>
    public void ConnectStreams(ZoneNativeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_tree.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_streams is not null) throw new InvalidOperationException("Terrain streams are already connected.");
            var streams = engine.CreateTerrainStreams();
            _streams = streams;
            _streamThread = Environment.CurrentManagedThreadId;
        }
    }

    public ZoneTerrainNode? ActiveReadNode { get { lock (_tree.Gate) return _activeRead?.Node; } }

    public ZoneTerrainNodeTextureSnapshot GetNodeTexture(ZoneTerrainNode node)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            var value = GetNodeTextureCore(node);
            return new(value.State, value.BaseHandle, value.AoHandle, value.BaseTransform,
                value.AoTransform, value.LastReadError, value.Diagnostics.ToArray());
        }
    }

    /// <summary>
    /// 300F17A0's exact signed-index/uint32 range arithmetic. Wrapping or zero-length ranges
    /// cannot be backed by the bounded native buffer and are rejected before starting a read.
    /// A negative authored index is ineligible for this owner, not an offset into the file header.
    /// </summary>
    public static ZoneTerrainNodeTextureRange GetReadRange(ZoneTerrainTextureContent content, short textureIndex)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(textureIndex);
        if (content.DeclaredLayerCount is not (1 or 2) || content.Layers.Count != content.DeclaredLayerCount)
            throw new InvalidDataException("Node streaming requires one or two complete terrain texture layers.");
        var first = content.Layers[0].ByteCount;
        var stride = content.DeclaredLayerCount == 1 ? first : (ulong)first + content.Layers[1].ByteCount;
        var offset = (ulong)textureIndex * stride + (uint)content.IndexSectionByteCount +
            (uint)content.DeclaredLayerCount * 12 + 16;
        var length = content.Flags == 1 ? stride : first;
        if (stride > uint.MaxValue || offset > uint.MaxValue || length == 0 || length > int.MaxValue ||
            offset + length > uint.MaxValue)
            throw new InvalidDataException("Terrain node byte range exceeds the owned native buffer limits.");
        return new((uint)offset, (int)length);
    }

    /// <summary>
    /// Starts one actual cover read. False means the single scratch owner is busy, or the node
    /// lacks eligible authored metadata/path/index. Call Pump or WaitForActiveRead to publish
    /// completion before the next terrain frame. The higher-level visibility scheduler is separate.
    /// </summary>
    public bool StartNodeRead(ZoneTerrainNode node, bool waitForCompletion = false)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            VerifyStreamThread();
            ThrowStreamFault();
            var streams = _streams ?? throw new InvalidOperationException("Connect the real engine terrain streams first.");
            if (_activeRead is not null) return false;
            if (node.Level > 4 || node.TextureIndexValue < 0) return false;
            var cell = node.X / 1024 * 8 + node.Y / 1024;
            if (_cells[cell] is not { } content || _cellPaths[cell] is not { } path) return false;
            ZoneNativeTerrainStreams.ValidatePath(path); // Recoverable admission, before native-call retention.
            var range = GetReadRange(content, node.TextureIndexValue);
            if (range.Length > _scratch.Length)
                throw new InvalidDataException("The terrain scratch allocation is shorter than the node read.");
            var texture = GetNodeTextureCore(node);
            try
            {
                var request = streams.StartRead(path, range.Offset, range.Length);
                _activeRead = new(node, texture, content, range, request);
                texture.State = ZoneTerrainNodeTextureState.Reading;
                texture.LastReadError = null;
                texture.Diagnostics.Clear();
                if (waitForCompletion) CompleteActiveRead(wait: true);
                return true;
            }
            catch (Exception error) { _streamFault = error; throw; }
        }
    }

    /// <summary>Pumps the real service and publishes this terrain's available completion.</summary>
    public void Pump()
    {
        lock (_tree.Gate)
        {
            VerifyStreaming();
            try
            {
                _streams!.Pump();
                CompleteActiveRead(wait: false);
            }
            catch (Exception error) { _streamFault = error; throw; }
        }
    }

    public void WaitForActiveRead()
    {
        lock (_tree.Gate)
        {
            VerifyStreaming();
            try { CompleteActiveRead(wait: true); }
            catch (Exception error) { _streamFault = error; throw; }
        }
    }

    private void CompleteActiveRead(bool wait)
    {
        if (_activeRead is not { } read) return;
        ZoneNativeTerrainReadResult? result;
        if (wait) result = read.Request.Wait();
        else if (!read.Request.TryGetResult(out result)) return;
        var texture = read.Texture;
        texture.LastReadError = result!.ErrorCode;
        if (result.ErrorCode == 0)
        {
            if (result.Data.Length != read.Range.Length)
                throw new InvalidDataException("The successful native node read did not fill its requested byte range.");
            result.Data.Span.CopyTo(_scratch);
            // 300F1310 consumes a pool entry even when the actual dedicated upload returns0.
            if (!_basePool.TryAcquire(out var baseHandle)) texture.Diagnostics.Add("The base texture pool is full.");
            texture.BaseHandle = unchecked((short)baseHandle);
            if (_aoPool.Count != 0 && read.Content.Flags == 1)
            {
                if (!_aoPool.TryAcquire(out var aoHandle)) texture.Diagnostics.Add("The ambient-occlusion texture pool is full.");
                texture.AoHandle = unchecked((short)aoHandle);
            }
            else texture.AoHandle = 0;
            texture.BaseTransform = CreateCompletedTransform(read.Node, read.Content.Layers[0].Dimension);
            // Native completion ignores CreateSectorTexturesFromBuffer's zero-handle result.
            texture.State = ZoneTerrainNodeTextureState.Complete;
        }
        else
        {
            texture.Diagnostics.Add($"Native terrain texture read failed with error 0x{result.ErrorCode:X8}.");
            texture.State = ZoneTerrainNodeTextureState.Idle;
        }
        // The low-level adapter already detached/released native callbacks and copied data.
        read.Request.Dispose();
        _activeRead = null;
    }

    // 300F14C0 and300E2A60. The existing300D7910 tree initializes translation entry0 to
    // (+0,+0,+0);300E6FD0 propagates that index to every child. Moving-origin translations
    // and nonzero base overrides require their production owners before extending this path.
    private static ZoneTerrainTextureTransform CreateCompletedTransform(ZoneTerrainNode node, ushort dimension)
    {
        var scale = dimension == 0 ? 1f : 1f - 1f / dimension;
        scale /= node.SizeMeters;
        var offsetU = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(node.Y * scale) ^ int.MinValue);
        var offsetV = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(node.X * scale) ^ int.MinValue);
        if (dimension != 0)
        {
            offsetU = 0.5f / dimension + offsetU;
            offsetV = 0.5f / dimension + offsetV;
        }
        var bounds = node.BoundsValue;
        var minimum = new Vector3(0f + bounds.Min.X, 0f + bounds.Min.Y, 0f + bounds.Min.Z);
        var maximum = new Vector3(0f + bounds.Max.X, 0f + bounds.Max.Y, 0f + bounds.Max.Z);
        return new(new(offsetU, offsetV), scale, minimum.Z, maximum.Z, new(minimum, maximum));
    }

    /// <summary>
    /// Drains any pending node read through normal completion before the 300E3580 texture reset.
    /// This retains pool transitions when completion occurs during retirement. Zero handles are
    /// not returned; scale and copied bounds survive. Authored indices remain unchanged.
    /// Unlike native early cancellation, this blocking retirement always permits successful
    /// pending reads to consume pool entries first. Exact queued cancellation is a separate gap.
    /// Render/procedural resources and the scheduler's active-node list have separate ownership.
    /// </summary>
    public void RetireNode(ZoneTerrainNode node, bool recursive)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            VerifyStreamThread();
            ThrowStreamFault();
            try { RetireNodeCore(node, recursive); }
            catch (Exception error) { _streamFault = error; throw; }
        }
    }

    private void RetireNodeCore(ZoneTerrainNode node, bool recursive)
    {
        if (ReferenceEquals(_activeRead?.Node, node)) CompleteActiveRead(wait: true);
        var texture = GetNodeTextureCore(node);
        if (texture.BaseHandle != 0 && !_basePool.Return(texture.BaseHandle))
            texture.Diagnostics.Add("The base handle was absent from its texture pool.");
        if (texture.AoHandle != 0 && !_aoPool.Return(texture.AoHandle))
            texture.Diagnostics.Add("The ambient-occlusion handle was absent from its texture pool.");
        texture.BaseHandle = texture.AoHandle = 0;
        texture.BaseTransform = texture.BaseTransform with { Offset = Vector2.Zero, MinimumHeight = 0, MaximumHeight = 0 };
        texture.AoTransform = texture.AoTransform with { Offset = Vector2.Zero, Scale = 1, MinimumHeight = 0, MaximumHeight = 0 };
        if (recursive)
            foreach (var child in node.Children) RetireNodeCore(child, true);
        texture.State = ZoneTerrainNodeTextureState.Idle;
    }

    private void RetireStreaming()
    {
        ThrowStreamFault();
        try
        {
            RetireNodeCore(_tree.Root, true);
            _streams?.Dispose();
            _streams = null;
            _nodeTextures.Clear();
            Array.Clear(_cellPaths);
        }
        catch (Exception error) { _streamFault = error; throw; }
    }

    private NodeTexture GetNodeTextureCore(ZoneTerrainNode node)
    {
        if (!_nodeTextures.TryGetValue(node, out var value)) _nodeTextures.Add(node, value = new());
        return value;
    }

    private void ValidateNode(ZoneTerrainNode node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        if (!ReferenceEquals(node.Owner, _tree)) throw new ArgumentException("The terrain node belongs to another tree.", nameof(node));
    }

    private void VerifyStreamThread()
    {
        if (_streamThread is { } thread && thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Connected terrain resources must mutate and retire on their engine simulation thread.");
    }

    private void VerifyStreaming()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VerifyStreamThread();
        ThrowStreamFault();
        if (_streams is null) throw new InvalidOperationException("Terrain streams have not been connected.");
    }

    private void ThrowStreamFault()
    {
        if (_streamFault is not null)
            throw new InvalidOperationException("Terrain stream ownership is faulted; retain its resources and engine until process termination.", _streamFault);
    }
}
