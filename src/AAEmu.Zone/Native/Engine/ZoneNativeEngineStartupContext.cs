using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Native.Engine;

/// <summary>
/// Owned Windows x64 IGameProvider context for the dedicated CSystem startup path.
/// The engine owner must retain this object through all borrowers and ISystem.Release,
/// check ThrowIfFaulted after native calls, and only then dispose it. No finalizer frees
/// callback memory behind a native borrower.
/// </summary>
internal sealed unsafe class ZoneNativeEngineStartupContext : IDisposable
{
    // XlGamesStartup table 39FB9858, default constructor 39215CE0. The complete local
    // contract is recorded in engine-startup-context-contract.md and its native exports.
    private const int StateSize = 0x2230;
    private const int SlotCount = 39;
    private readonly object _gate = new();
    private readonly Delegate[] _callbacks = new Delegate[SlotCount];
    private nint _state;
    private nint _table;
    private nint _launcherPath;
    private nint _emptyString;
    private ExceptionDispatchInfo? _fault;
    private bool _disposed;

    internal ZoneNativeEngineStartupContext()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The engine startup context requires the x64 ABI.");
        try
        {
            _state = Allocate(StateSize);
            _table = Allocate(SlotCount * 8);
            _launcherPath = Marshal.StringToHGlobalAnsi(@".\XIGNCODE\KO");
            _emptyString = Marshal.StringToHGlobalAnsi("");
            Marshal.WriteInt16(_state, 0x36, 3);
            Marshal.WriteByte(_state, 0x2223, 0xFF);

            // Explicit byte/int/pointer signatures preserve the widths defined by the
            // native leaves. Empty native methods consume no additional arguments.
            Bind(0x00, new DeleteCall((self, flags) => Invoke(self, 0x00, flags)));
            Bind(0x08, new ByteCall(self => (byte)Invoke(self, 0x08)));
            Bind(0x10, new IntegerCall(self => (int)Invoke(self, 0x10)));
            Bind(0x18, new IntegerCall(self => (int)Invoke(self, 0x18)));
            Bind(0x20, new IntegerCall(self => (int)Invoke(self, 0x20)));
            Bind(0x28, new PointerCall(self => Invoke(self, 0x28)));
            Bind(0x30, new IntegerCall(self => (int)Invoke(self, 0x30)));
            Bind(0x38, new VoidCall(self => Invoke(self, 0x38)));
            Bind(0x40, new ByteCall(self => (byte)Invoke(self, 0x40)));
            Bind(0x48, new ByteCall(self => (byte)Invoke(self, 0x48)));
            Bind(0x50, new VoidCall(self => Invoke(self, 0x50)));
            Bind(0x58, new SetCellCall((self, index, value) => (byte)Invoke(self, 0x58, index, value)));
            Bind(0x60, new CopyCellsCall((self, output) => Invoke(self, 0x60, output)));
            Bind(0x68, new IntegerCall(self => (int)Invoke(self, 0x68)));
            Bind(0x70, new VersionCall((self, version, revision) => Invoke(self, 0x70, version, revision)));
            Bind(0x78, new PointerCall(self => Invoke(self, 0x78)));
            Bind(0x80, new ByteCall(self => (byte)Invoke(self, 0x80)));
            Bind(0x88, new ByteCall(self => (byte)Invoke(self, 0x88)));
            Bind(0x90, new ByteCall(self => (byte)Invoke(self, 0x90)));
            Bind(0x98, new ByteCall(self => (byte)Invoke(self, 0x98)));
            Bind(0xA0, new VoidCall(self => Invoke(self, 0xA0)));
            Bind(0xA8, new VoidCall(self => Invoke(self, 0xA8)));
            Bind(0xB0, new VoidCall(self => Invoke(self, 0xB0)));
            Bind(0xB8, new VoidCall(self => Invoke(self, 0xB8)));
            Bind(0xC0, new ByteCall(self => (byte)Invoke(self, 0xC0)));
            Bind(0xC8, new DisplayModeCall((self, count) => (int)Invoke(self, 0xC8, count)));
            Bind(0xD0, new IntegerCall(self => (int)Invoke(self, 0xD0)));
            Bind(0xD8, new PointerCall(self => Invoke(self, 0xD8)));
            Bind(0xE0, new PointerCall(self => Invoke(self, 0xE0)));
            Bind(0xE8, new IntegerCall(self => (int)Invoke(self, 0xE8)));
            Bind(0xF0, new PointerCall(self => Invoke(self, 0xF0)));
            Bind(0xF8, new PointerCall(self => Invoke(self, 0xF8)));
            Bind(0x100, new PointerCall(self => Invoke(self, 0x100)));
            Bind(0x108, new PointerCall(self => Invoke(self, 0x108)));
            Bind(0x110, new PointerCall(self => Invoke(self, 0x110)));
            Bind(0x118, new PointerCall(self => Invoke(self, 0x118)));
            Bind(0x120, new PointerCall(self => Invoke(self, 0x120)));
            Bind(0x128, new PointerCall(self => Invoke(self, 0x128)));
            Bind(0x130, new PointerCall(self => Invoke(self, 0x130)));
            Marshal.WriteIntPtr(_state, _table);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Pointer
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _fault?.Throw();
                return _state;
            }
        }
    }

    internal void ThrowIfFaulted()
    {
        lock (_gate)
        {
            _fault?.Throw();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    // All reverse calls enter one guarded body, including receiver validation and
    // state mutations. Pointer arguments remain a native ABI requirement: nonnull
    // version/output pointers must address 16/44 readable/writable bytes respectively.
    private nint Invoke(nint receiver, int slot, nint argument = 0, long value = 0)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (receiver != _state)
                    throw new InvalidOperationException("The engine used a foreign startup context receiver.");
                if (slot == 0)
                {
                    // CSystem only borrows this object. Native 39216480 returns this
                    // and deletes iff flags&1; it cannot free managed reverse thunks.
                    // Treat either native destruction variant as a terminal ownership
                    // violation and retain memory until the engine owner's Dispose.
                    RecordFault(new InvalidOperationException(
                        $"Native destruction of the borrowed startup context is unsupported (flags {(byte)argument:X2})."));
                    return receiver;
                }
                if (_fault is not null) return 0;

                switch (slot)
                {
                    case 0x08: case 0x10: case 0x20:
                    case 0x80: case 0x88: case 0x90: case 0x98:
                    case 0xA0: case 0xA8: case 0xB0: case 0xB8:
                        return 0;
                    case 0x18: return Marshal.ReadByte(_state, 0x2223);
                    case 0x28: return Marshal.ReadInt32(_state, 0x2224) > 0 ? Nonempty(0x878) : 0;
                    case 0x30: return Marshal.ReadInt32(_state, 0x2224);
                    case 0x38:
                        throw new NotSupportedException(
                            "IGameProvider launcher authentication (+38) is outside engine-only startup; the engine owner is poisoned.");
                    case 0x40: return 1;
                    case 0x48: return Marshal.ReadByte(_state, 0x34);
                    case 0x50: ResetLauncherState(); return 0;
                    case 0x58:
                        var index = (int)argument;
                        if ((uint)index >= 11)
                            throw new ArgumentOutOfRangeException(nameof(index), index,
                                "The startup context owns eleven state cells; this index cannot be stored.");
                        if (index == 1 && Marshal.ReadInt32(_state, 0x0C) != 0) return 0;
                        Marshal.WriteInt32(_state, 8 + index * 4, unchecked((int)value));
                        return 1;
                    case 0x60:
                        if (argument == 0) throw new ArgumentNullException("output");
                        new ReadOnlySpan<byte>((void*)(_state + 8), 0x2C).CopyTo(new Span<byte>((void*)argument, 0x2C));
                        return argument;
                    case 0x68: return 1; // Native locale value, not an inferred language.
                    case 0x70: FormatVersion(argument, value); return 0;
                    case 0x78: return _state + 0x38;
                    case 0xC0: return 2;
                    case 0xC8: return (ushort)argument - 1; // MOVZX EAX,DX; DEC EAX. Caller consumes AX.
                    case 0xD0: return Marshal.ReadByte(_state, 0x222C) == 1 ? 3 : 0;
                    case 0xD8: return Marshal.ReadByte(_state, 0x222C) == 1 ? _launcherPath : _emptyString;
                    case 0xE0: return Nonempty(0x903);
                    case 0xE8: return Marshal.ReadInt32(_state, 0x2228);
                    case 0xF0: return Nonempty(0x8B8);
                    case 0xF8: return Nonempty(0x1103);
                    case 0x100: return Nonempty(0x1183);
                    case 0x108: return Nonempty(0x11A3);
                    case 0x110: return Nonempty(0x11C3);
                    case 0x118: return Nonempty(0x11E3);
                    case 0x120: return Nonempty(0x1203);
                    case 0x128: return Nonempty(0x1223);
                    case 0x130: return Nonempty(0x1A23);
                    default: throw new InvalidOperationException("Unknown startup callback slot.");
                }
            }
        }
        catch (Exception error)
        {
            // A managed exception must never unwind through a native engine frame.
            RecordFault(error);
            return 0;
        }
    }

    private nint Nonempty(int offset) => Marshal.ReadByte(_state, offset) != 0 ? _state + offset : 0;

    private void ResetLauncherState()
    {
        Marshal.WriteByte(_state, 0x34, 0);
        Marshal.WriteByte(_state, 0x2223, 0xFF);
        Marshal.WriteInt16(_state, 0x36, (short)((Marshal.ReadInt16(_state, 0x36) & 0xFFFB) | 2));
        new Span<byte>((void*)(_state + 0x78), 0x800).Clear();
        // These buffers are contiguous from 8B8 through 2222 inclusive. The host
        // buffer at 878, both scalars, alternate-launcher byte and state cells survive.
        new Span<byte>((void*)(_state + 0x8B8), 0x2223 - 0x8B8).Clear();
    }

    private void FormatVersion(nint version, long revision)
    {
        if (version == 0) throw new ArgumentNullException(nameof(version));
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{Marshal.ReadInt32(version, 12)}.{Marshal.ReadInt32(version, 8)}.{Marshal.ReadInt32(version, 4)}.{Marshal.ReadInt32(version)}.KX (r.{unchecked((int)revision)})");
        var destination = new Span<byte>((void*)(_state + 0x38), 0x40);
        // Do not turn an over-capacity native formatted string into silent truncation.
        if (text.Length >= destination.Length)
        {
            destination[0] = 0;
            throw new InvalidOperationException("The native startup version exceeds its 64-byte buffer.");
        }
        var written = Encoding.ASCII.GetBytes(text, destination);
        destination[written] = 0;
    }

    private void RecordFault(Exception error) =>
        Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(error), null);

    private void Bind(int offset, Delegate callback)
    {
        _callbacks[offset / 8] = callback;
        Marshal.WriteIntPtr(_table, offset, Marshal.GetFunctionPointerForDelegate(callback));
    }

    private static nint Allocate(int size)
    {
        var pointer = (nint)NativeMemory.AllocZeroed((nuint)size);
        if (pointer == 0) throw new OutOfMemoryException();
        return pointer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            NativeMemory.Free((void*)_state);
            NativeMemory.Free((void*)_table);
            Marshal.FreeHGlobal(_launcherPath);
            Marshal.FreeHGlobal(_emptyString);
            _state = _table = _launcherPath = _emptyString = 0;
            Array.Clear(_callbacks);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint DeleteCall(nint self, byte flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte ByteCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int IntegerCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint PointerCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VoidCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte SetCellCall(nint self, int index, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint CopyCellsCall(nint self, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VersionCall(nint self, nint version, long revision);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int DisplayModeCall(nint self, ushort count);
}
