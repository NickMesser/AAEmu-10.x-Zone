# Managed collision publication lifetime

`ZoneTerrainHeightField.PublicationRevision` and
`ZoneManagedStaticGeometry.PublicationRevision` are monotonically increasing `ulong`
tokens on the actual query owners. They are managed lifetime bookkeeping; they are not
native fields, numeric-parity evidence, or a substitute for live coverage admission.
Compare tokens only with the identity of the resource which produced them.

Every token read and mutation runs under its resource's existing gate. A mutation advances
the token before its first write, after normal input validation/allocation, so a failed
partial publication cannot leave the previous token current. Exhaustion refuses further
writes before changing ownership; tokens never wrap or restart on a clear.

| Owner operation | Publication effect |
| --- | --- |
| Terrain node-tree creation | Advances on the new structural publication. |
| Nonempty `LoadCell` | Advances once for the atomic sample replacement, bounds and seams, even with equal source data. |
| Engine `InstallCellSamples` | Advances when its new sample owner is installed. |
| Later engine `CopyCellSeams` | Advances separately when at least one edge has source and destination samples. This write-pass fence is conservative even when quantization retains identical bytes. |
| Terrain present-cell unload | Advances before retiring samples; reloading the same address cannot reuse the old token. |
| Empty terrain chunk, absent unload, seam pass without writable edges | Leaves the token unchanged. |
| Static mesh or primitive `AddInstance` | Advances for the new immutable instance. |
| Static present `RemoveInstance` | Advances before removal. |
| Static nonempty `Clear` | Advances once; handles and tokens are never reused. |
| Static absent removal or empty clear | Leaves the token unchanged. |
| Queries, rejected inputs, callback-cache reset | Do not publish geometry and leave these tokens unchanged. |

The terrain audit covers the only current sample writers: content-only load,
the engine's staged sample install, the later X/Y seam writes, and unload. Public nodes
return copied sample arrays and expose no writable hole/height view. Independent node
bounds/texture refreshes do not change the sample geometry used by managed physics.

Static meshes copy their vertex/index/material inputs and expose copied triangles.
Primitive dimensions, per-instance transforms and admission bounds are immutable. This
owner currently has no mutable eligibility-flag API: character/ray eligibility is
represented by membership in separate `Geometry` and `RayGeometry` instances. Consumers
which use both must capture and validate both tokens. Existing scene registration and
retirement paths ultimately use the hooked add/remove/clear methods.

These tokens do not cover independent dynamic actor poses, dimensions or part flags.
Those remain in the living backend and animated-collider owner, including
`ApplyAnimatedColliderUpdate`. Terrain surface-material bindings and the native callback
cache contents are also separate from managed sample/instance ownership. A caller must
not use these tokens to certify an unowned mutation in another resource. Navigation
also uses its actual water publication/retirement owner and graph revision, in addition
to live terrain/collision coverage checks.

The scene now adds [brush-cell and composed-cell publications](managed-cell-collision-coverage.md).
Their revisions also observe bodyless retirement. They retain exact source rows and live owned
handles rather than treating total geometry counts as evidence. Navigation captures all six
physical publication tokens before work and checks them before another request. A lost water
source invalidates global completeness; a source contour may cross into a neighboring cell.

The defect which motivated this seam was reproduced with the real composed managed
Approach/Predictive/Puppet/proxy/body test fixture: after a terrain cell unload, a route
guard checking only graph freshness accepted another physical request and moved its Unit.
The preserved pre-fix evidence is
`D:/aaemu-runtime/research/nav-review/terrain-unload-control.json`. Navigation now owns
the capture/revalidation policy; these resources only report their actual publication
changes.

`ManagedCollisionPublicationTests` has seven controls for equal-address replacement,
actual unload, staged seam publication and its neighbor-height effect, no-op semantics,
query stability, invalid-input atomicity, immutable static membership, and exhaustion
before writes. The existing terrain and static-geometry numeric tests remain separate.
