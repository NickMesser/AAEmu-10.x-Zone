using System.Numerics;
using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.Entities;

/// <summary>Copied native world pose of an owned, parentless entity; no native pointers escape.</summary>
public readonly record struct ZoneNativeEntityPose(Vector3 Position, Quaternion Orientation, float Scale);

public sealed unsafe partial class ZoneNativeEntityServices
{
    public sealed partial class Entity
    {
        /// <summary>
        /// Reads the actual native cached world pose through IEntity+C8. This adapter admits only
        /// parentless, uniformly scaled entities with an exclusively separate plain physical body.
        /// It does not derive the entity transform from that body's status.
        /// </summary>
        public ZoneNativeEntityPose CapturePose()
        {
            ObjectDisposedException.ThrowIf(Retired, this);
            using var operation = _owner.Enter();
            return CapturePoseCore();
        }

        /// <summary>
        /// Publishes position and orientation through native SetPosRotScale, preserving the
        /// current scale and ordinary transform callbacks. A physics caller explicitly invokes
        /// this after obtaining its body's pose. This operation does not move or step that body.
        /// The returned pose reflects native tolerances and any native transform callback effects.
        /// </summary>
        public ZoneNativeEntityPose PublishPose(Vector3 position, Quaternion orientation)
        {
            ObjectDisposedException.ThrowIf(Retired, this);
            using var operation = _owner.Enter();
            if (!Finite(position) || !Finite(new(orientation.X, orientation.Y, orientation.Z)) ||
                !float.IsFinite(orientation.W) || !float.IsFinite(orientation.LengthSquared()) ||
                orientation.LengthSquared() <= 0)
                throw new ArgumentException("Published entity pose must be finite with a nonzero rotation.");
            RequirePoseOwner();
            var scale = _owner.Native(() =>
            {
                // IEntity+120 / 32016BE0 returns this+50, the actual local scale vector.
                var address = _owner.Bind<PointerCall>(Pointer, 0x120, 0x16be0)(Pointer);
                _owner.Require(address == Pointer + 0x50, "The native scale getter returned an unexpected address.");
                return *(Vector3*)address;
            });
            _owner.Require(Finite(scale) && scale.X > 0 && scale.X == scale.Y && scale.X == scale.Z,
                "Pose publication requires the owned entity's existing positive uniform scale.");
            _owner.Native(() =>
            {
                var nativePosition = position;
                var nativeOrientation = orientation;
                var nativeScale = scale;
                // IEntity+130 / 32012940 performs its own position/scale tolerance and q/-q
                // rotation checks. Flags0 requests an ordinary update. Do not guess attachment
                // or physics-origin flags, synthesize event0, or write the transform fields.
                _owner.Bind<SetPoseCall>(Pointer, 0x130, 0x12940)(Pointer,
                    &nativePosition, &nativeOrientation, &nativeScale, 0);
                return 0;
            });
            // Native +368 / 32013100 rebuilds cached transforms, invokes its spatial work and
            // dispatches event0 when a change passes the native comparisons. Revalidate before
            // reading anything retained across those callbacks, and return the resulting state.
            return CapturePoseCore();
        }

        private ZoneNativeEntityPose CapturePoseCore()
        {
            RequirePoseOwner();
            var copied = _owner.Native(() =>
            {
                NativeWorldPose pose = default;
                // IEntity+C8 / 3200DD10 copies all eight floats from transform data+78..94.
                var result = _owner.Bind<CapturePoseCall>(Pointer, 0xc8, 0xdd10)(Pointer, &pose);
                _owner.Require(result == (nint)(&pose), "The native pose getter returned an unexpected output buffer.");
                return pose;
            });
            _owner.Require(Finite(copied.Position) &&
                Finite(new(copied.Orientation.X, copied.Orientation.Y, copied.Orientation.Z)) &&
                float.IsFinite(copied.Orientation.W) && float.IsFinite(copied.Orientation.LengthSquared()) &&
                copied.Orientation.LengthSquared() > 0 && float.IsFinite(copied.Scale) && copied.Scale > 0,
                "The owned native entity returned an invalid world pose.");
            return new(copied.Position, copied.Orientation, copied.Scale);
        }

        private void RequirePoseOwner()
        {
            // This includes full-ID lookup before pointer access and exact character/skeleton
            // identity. Neither native proxy nor skeleton may also own physicalization.
            _owner.RequirePlainPhysicsOwner(this);
            _owner.Require((Marshal.ReadInt32(Pointer + 0x10) & 8) != 0,
                "Native entity pose access requires completed entity initialization.");
            // Native setter consumes local coordinates; parentless ownership makes those world
            // coordinates. Parent/attachment transform publication is outside this initial seam.
            _owner.Require(_owner.Native(() =>
                _owner.Bind<PointerCall>(Pointer, 0xa0, 0x16b80)(Pointer)) == 0,
                "The initial native entity pose owner does not admit an attached parent.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWorldPose
    {
        public Quaternion Orientation;
        public Vector3 Position;
        public float Scale;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CapturePoseCall(nint entity, NativeWorldPose* output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetPoseCall(nint entity, Vector3* position, Quaternion* orientation,
        Vector3* scale, uint flags);
}
