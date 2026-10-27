using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Borrows the real CSystem stream engine for one owned terrain read at a time. Call from the
/// engine's simulation thread. Pump and Wait may run native completion work synchronously.
/// Dispose drains I/O before cancelling and releasing it; it has no forced timeout or finalizer.
/// An uncertain native failure retains the child, buffers and callbacks until process termination.
/// </summary>
public sealed unsafe class ZoneNativeTerrainStreams : IDisposable
{
    private static Exception? _callbackDispatchFault;
    private readonly ZoneNativeEngine.StreamLease _lease;
    private readonly uint _nativeThread;
    private readonly CreateCall _create;
    private readonly PumpCall _pump;
    private readonly StreamCall _cancel;
    private readonly StreamCall _release;
    private readonly WaitCall _wait;
    private readonly BytesReadCall _bytesRead;
    private ReadRequest? _active;
    private bool _disposed;

    // Root constructs and roots this child while already holding the shared engine gate.
    internal ZoneNativeTerrainStreams(ZoneNativeEngine.StreamLease lease)
    {
        _lease = lease;
        _nativeThread = GetCurrentThreadId();
        var module = lease.SystemModule;
        var engine = lease.StreamEngine;
        if (engine == 0 || Marshal.ReadIntPtr(engine) != module + 0x11ace8 ||
            unchecked((uint)Marshal.ReadInt32(engine + 0x200)) != _nativeThread)
            throw new InvalidDataException("The terrain stream engine has an unsupported identity or owner thread.");
        RequireSlot(engine, 0, module + 0x97770);
        RequireSlot(engine, 0x18, module + 0x987d0);
        _create = Marshal.GetDelegateForFunctionPointer<CreateCall>(module + 0x97770);
        _pump = Marshal.GetDelegateForFunctionPointer<PumpCall>(module + 0x987d0);
        _cancel = Marshal.GetDelegateForFunctionPointer<StreamCall>(module + 0x98fd0);
        _release = Marshal.GetDelegateForFunctionPointer<StreamCall>(module + 0x98f10);
        _wait = Marshal.GetDelegateForFunctionPointer<WaitCall>(module + 0x98dd0);
        _bytesRead = Marshal.GetDelegateForFunctionPointer<BytesReadCall>(module + 0x98e80);
    }

    /// <summary>
    /// Starts a bounded cover.ctc byte-range read using the native terrain request parameters.
    /// Zero length (native's read-to-end convention) is deliberately unsupported for owned buffers.
    /// Complete or dispose the returned request before starting another read.
    /// </summary>
    public ReadRequest StartRead(string virtualPath, uint offset, int length)
    {
        ValidatePath(virtualPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if ((ulong)offset + (uint)length > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), "The requested native byte range exceeds uint32.");
        var path = Encoding.ASCII.GetBytes(virtualPath + '\0');
        using var operation = Enter();
        if (_active is not null)
            throw new InvalidOperationException("Complete or dispose the current terrain read first.");
        var request = new ReadRequest(this, length);
        _active = request; // Root all reverse callbacks and buffer storage before entering native.
        try
        {
            var parameters = new NativeRequest
            {
                Value08 = 1,
                Value10 = 1000,
                Buffer = request.Buffer,
                Offset = offset,
                Length = (uint)length
            };
            nint stream = 0;
            fixed (byte* name = "3DEngine\0"u8)
            fixed (byte* file = path)
                _create(_lease.StreamEngine, &stream, name, file, request.Callback, &parameters);
            request.Stream = stream; // Returned smart pointer owns exactly one reference.
            if (stream == 0 || Marshal.ReadIntPtr(stream) != _lease.SystemModule + 0x11aa38)
                throw new InvalidDataException("The native terrain read returned an unsupported stream.");
            RequireSlot(stream, 8, _lease.SystemModule + 0x98f10);
            RequireSlot(stream, 0x20, _lease.SystemModule + 0x98e80);
            RequireSlot(stream, 0x30, _lease.SystemModule + 0x98fd0);
            RequireSlot(stream, 0x78, _lease.SystemModule + 0x98dd0);
            Check(request);
            return request;
        }
        catch (Exception error) { _lease.Poison(error); throw; }
    }

    // Shared preflight lets the composing node owner reject unsupported authored paths before
    // entering the terminal native-call boundary or retaining a failed node operation.
    internal static void ValidatePath(string virtualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        if (virtualPath.Length > 259 || virtualPath.Any(character => character == 0 || character > 0x7f))
            throw new ArgumentException("The native terrain path must be terminated ASCII within MAX_PATH.", nameof(virtualPath));
        if (!virtualPath.Replace('\\', '/').EndsWith("/client/terrain/cover.ctc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This adapter reads terrain cover.ctc resources.", nameof(virtualPath));
    }

    /// <summary>Dispatches available native completions on the owner thread and copies a completed result.</summary>
    public void Pump()
    {
        using var operation = Enter();
        try
        {
            //365987D0 ->36597350, flag1 bypasses the callback time-budget early exit.
            _pump(_lease.StreamEngine, 1);
            _lease.ThrowIfFaulted();
            if (_active is { } request)
            {
                Check(request);
                if (request.NativeCompleted) Finish(request, discard: false);
            }
        }
        catch (Exception error) { _lease.Poison(error); throw; }
    }

    private ZoneNativeTerrainReadResult Wait(ReadRequest request)
    {
        using var operation = Enter();
        if (request.Discarded) throw new ObjectDisposedException(nameof(ReadRequest));
        if (request.Result is { } result) return result;
        RequireCurrent(request);
        try
        {
            Drain(request);
            Finish(request, discard: false);
            return request.Result!;
        }
        catch (Exception error) { _lease.Poison(error); throw; }
    }

    private bool TryGetResult(ReadRequest request, out ZoneNativeTerrainReadResult? result)
    {
        using var operation = Enter();
        if (request.Discarded) throw new ObjectDisposedException(nameof(ReadRequest));
        result = request.Result;
        return result is not null;
    }

    private void Discard(ReadRequest request)
    {
        using var operation = Enter();
        if (request.Retired) return;
        RequireCurrent(request);
        try
        {
            Drain(request);
            Finish(request, discard: true);
        }
        catch (Exception error) { _lease.Poison(error); throw; }
    }

    private void Drain(ReadRequest request)
    {
        Check(request);
        // Cancel-before-start only detaches a callback and leaves a queued proxy. Waiting while
        // the callback remains installed gives the borrowed buffer an explicit completion fence.
        if (!request.NativeCompleted) _wait(request.Stream, -1);
        Check(request);
        if (!request.NativeCompleted)
            throw new InvalidDataException("Native terrain Wait returned without the completion callback.");
    }

    private void Finish(ReadRequest request, bool discard)
    {
        Check(request);
        ZoneNativeTerrainReadResult? result = null;
        if (!discard)
        {
            byte[] data = [];
            if (request.ErrorCode == 0)
            {
                var count = _bytesRead(request.Stream, 0);
                if (count > (uint)request.Length)
                    throw new InvalidDataException("Native terrain read exceeded its owned buffer.");
                data = new byte[(int)count];
                Marshal.Copy(request.Buffer, data, 0, data.Length);
            }
            result = new ZoneNativeTerrainReadResult(request.ErrorCode, data);
        }
        // Completion36597350 cleared callback+50 before invoking +10, and returned before this
        // code runs. Cancel can safely wait/reenter; keep every callback/buffer root through Release.
        _cancel(request.Stream);
        Check(request);
        _release(request.Stream);
        Check(request);
        request.Stream = 0;
        request.FreeStorage();
        request.Result = result;
        request.Discarded = discard;
        request.Retired = true;
        _active = null;
    }

    private void Check(ReadRequest request)
    {
        _lease.ThrowIfFaulted();
        if (Volatile.Read(ref _callbackDispatchFault) is { } dispatchError)
            throw new InvalidOperationException("The native terrain callback could not resolve its retained owner.", dispatchError);
        if (request.CallbackFault is { } error)
            throw new InvalidOperationException("The native terrain stream callback failed.", error);
        if (request.ObservedStream != 0 && request.ObservedStream != request.Stream)
            throw new InvalidDataException("The completion callback supplied another native stream.");
    }

    private IDisposable Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _lease.Enter();
    }

    private void RequireCurrent(ReadRequest request)
    {
        if (!ReferenceEquals(_active, request))
            throw new InvalidOperationException("The terrain read no longer belongs to this active stream slot.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        using var operation = Enter();
        try
        {
            if (_active is { } request)
            {
                Drain(request);
                Finish(request, discard: true);
            }
            // Completed proxies can remain in the real engine's release queue. Their callback
            // is detached and their borrowed buffer is no longer used; CSystem owns that queue.
            _lease.Release();
            _disposed = true;
        }
        catch (Exception error) { _lease.Poison(error); throw; }
    }

    private static void RequireSlot(nint receiver, int offset, nint expected)
    {
        if (Marshal.ReadIntPtr(Marshal.ReadIntPtr(receiver) + offset) != expected)
            throw new InvalidDataException("Unsupported native terrain stream virtual function.");
    }

    /// <summary>An owned read handle. Disposal synchronously drains pending I/O; no user code runs in native callbacks.</summary>
    public sealed class ReadRequest : IDisposable
    {
        private readonly ZoneNativeTerrainStreams _owner;
        private nint _storage;
        private GCHandle _root;
        private Exception? _callbackFault;
        private bool _nativeCompleted;
        internal nint Stream;
        internal nint ObservedStream;
        internal nint Buffer { get; private set; }
        internal nint Callback => _storage + 24;
        internal int Length { get; }
        internal uint ErrorCode;
        internal bool NativeCompleted => Volatile.Read(ref _nativeCompleted);
        internal Exception? CallbackFault => Volatile.Read(ref _callbackFault);
        internal ZoneNativeTerrainReadResult? Result;
        internal bool Retired;
        internal bool Discarded;

        internal ReadRequest(ZoneNativeTerrainStreams owner, int length)
        {
            _owner = owner;
            Length = length;
            try
            {
                Buffer = (nint)NativeMemory.AllocZeroed((nuint)length);
                _storage = (nint)NativeMemory.AllocZeroed(40);
                if (Buffer == 0 || _storage == 0) throw new OutOfMemoryException();
                _root = GCHandle.Alloc(this);
                Marshal.WriteIntPtr(_storage, Marshal.GetFunctionPointerForDelegate(DestructorThunk));
                Marshal.WriteIntPtr(_storage + 8, Marshal.GetFunctionPointerForDelegate(AsyncThunk));
                Marshal.WriteIntPtr(_storage + 16, Marshal.GetFunctionPointerForDelegate(CompleteThunk));
                Marshal.WriteIntPtr(Callback, _storage);
                Marshal.WriteIntPtr(Callback + 8, GCHandle.ToIntPtr(_root));
            }
            catch { FreeStorage(); throw; }
        }

        /// <summary>Waits on the real engine and returns an independent managed copy of the completed bytes.</summary>
        public ZoneNativeTerrainReadResult Wait() => _owner.Wait(this);

        /// <summary>Gets an already copied result; call Pump or Wait to dispatch native completion.</summary>
        public bool TryGetResult(out ZoneNativeTerrainReadResult? result) => _owner.TryGetResult(this, out result);

        public void Dispose()
        {
            if (Retired) return;
            _owner.Discard(this);
        }

        internal void FreeStorage()
        {
            if (_root.IsAllocated) _root.Free();
            NativeMemory.Free((void*)_storage);
            NativeMemory.Free((void*)Buffer);
            _storage = 0;
            Buffer = 0;
        }

        private static readonly DeleteCallback DestructorThunk = UnexpectedDelete;
        private static readonly CompletionCallback AsyncThunk = AsyncComplete;
        private static readonly CompletionCallback CompleteThunk = Complete;

        private static ReadRequest FromCallback(nint callback) =>
            (ReadRequest)GCHandle.FromIntPtr(Marshal.ReadIntPtr(callback + 8)).Target!;

        private static nint UnexpectedDelete(nint callback, uint flags)
        {
            try
            {
                FromCallback(callback).RecordFault(new InvalidOperationException(
                    "The native stream attempted to delete a borrowed terrain callback."));
            }
            catch (Exception error) { Interlocked.CompareExchange(ref _callbackDispatchFault, error, null); }
            return callback;
        }

        // Native terrain callback+08 is30020AD0, an exact no-op (including final-release errors).
        private static void AsyncComplete(nint callback, nint stream, uint error) { }

        private static void Complete(nint callback, nint stream, uint error)
        {
            ReadRequest? request = null;
            try
            {
                request = FromCallback(callback);
                if (GetCurrentThreadId() != request._owner._nativeThread)
                    throw new InvalidOperationException("Terrain completion ran outside the stream engine's owner thread.");
                if (request.NativeCompleted || stream == 0)
                    throw new InvalidDataException("Invalid duplicate or null native terrain completion.");
                request.ObservedStream = stream;
                request.ErrorCode = error;
                Volatile.Write(ref request._nativeCompleted, true);
            }
            catch (Exception exception)
            {
                if (request is not null) request.RecordFault(exception);
                else Interlocked.CompareExchange(ref _callbackDispatchFault, exception, null);
            }
        }

        private void RecordFault(Exception error)
        {
            Interlocked.CompareExchange(ref _callbackFault, error, null);
            // Another adapter's Pump can dispatch this request. Poison the shared owner now,
            // even when that other adapter has no active request of its own to inspect.
            _owner._lease.Poison(error);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct NativeRequest
    {
        [FieldOffset(8)] internal ulong Value08;
        [FieldOffset(16)] internal ulong Value10;
        [FieldOffset(24)] internal nint Buffer;
        [FieldOffset(32)] internal uint Offset;
        [FieldOffset(36)] internal uint Length;
        // +00/+28 are zero. Producer leaves the unused final four bytes unspecified; this
        // owned request initializes them instead of exposing uninitialized managed memory.
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateCall(nint engine, nint* result, byte* label, byte* path, nint callback, NativeRequest* request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PumpCall(nint engine, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StreamCall(nint stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WaitCall(nint stream, int timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint BytesReadCall(nint stream, byte wait);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DeleteCallback(nint callback, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CompletionCallback(nint callback, nint stream, uint error);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

/// <summary>Native error code (zero on success) and bytes copied out of the retired native read buffer.</summary>
public sealed record ZoneNativeTerrainReadResult(uint ErrorCode, ReadOnlyMemory<byte> Data);
