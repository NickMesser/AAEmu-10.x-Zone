# Managed entity direction vectors

`ZoneManagedEntityOrientation` implements the value math of the original entity
`GetDirectionVector` binding. The scene supplies its actual current entity rotation and scale,
or its composed world transform, after checking the exact actor generation.

The binding at `3203CB90` resolves the entity through `3202E630`. An absent entity returns no
Lua values. Its default basis is Y. A numeric first argument passes through a native float and
then truncates to an integer: 0 selects X, 1 selects Y, and 2 selects Z. Other integers retain Y.
An absent or nonnumeric argument also retains Y; numeric strings are not converted. Nonfinite
or overflowing float-to-int conversions produce the invalid integer sentinel and therefore Y.

The entity's vtable at `320CD998`, offset `C0`, points to `3200DCD0`. That getter copies the
48-byte cached world Matrix34 from `entity.transformData + 8`. The binding multiplies its
selected basis by this matrix with float operations. Translation is excluded. The result keeps
the matrix's scale, mirroring and shear.

| Source | Applied behavior |
| --- | --- |
| `32012F50` | Builds the parentless matrix from stored quaternion, scale and position. |
| `32016930` | Expands the raw quaternion using float doubling and double intermediates. |
| `320155D0` | Multiplies the diagonal scale matrix, including its zero terms. |
| `32013100` | Publishes that matrix to the cache and composes an admitted parent's transform. |
| `3203CB90` | Selects and returns the cached world-matrix column without normalization. |

The quaternion overload preserves these operations. For example, stored quaternion
`(0,0,0.5,0.5)`, scale 3 and axis 0 produce `(1.5,1.5,0)`. The matrix overload accepts
System.Numerics row-vector transforms: their rows correspond to the Cry Matrix34 columns.
It preserves a supplied parent matrix's nonuniform scale and shear. A scene with only a local
pose cannot substitute that pose for an unavailable composed parent transform.

The public helper APIs are:

```csharp
int AxisFromScriptArgument(object? value);
Vector3 GetDirectionVector(Quaternion rotation, float uniformScale, int axis = 1);
Vector3 GetDirectionVector(Matrix4x4 world, int axis = 1);
```

The managed script-host binding uses `AxisFromScriptArgument`, preserving the native numeric
default/conversion behavior. Ground NPC admission still owns its positive-scale, upright and
attachment limits; the low-level matrix helper also supports finite mirrored matrices.

Three focused tests cover float-before-integer axis conversion, invalid/default axes, raw
quaternions, scale, equivalent quaternion signs, mirrored signed zero, and a composed parent
matrix with nonuniform scale/shear and independent translation.

The local offline oracle executes original PE instructions under Unicorn. Its source binary is
`cryentitysystem_dedicate.dll`, SHA-256:

```text
eadc46ff915dca687700aa15d7f3aa4892954336da5d65cb523918288ea3e413
```

There are 33 native controls: 18 quaternion/scale/axis controls built by the original
`32012F50` instructions, 14 cached-matrix argument controls, and one absent-entity control.
The entity lookup, FunctionHandler operations, exact 48-byte cached copy at the CRT memcpy
boundary, and script-vector table return/reference counts use declared ABI callbacks. These
callbacks do not execute a Lua VM or load engine modules.

All 32 value-bearing controls match the managed helper component-for-component, with maximum
vector distance 0 and zero differing float bit patterns. The initial comparison exposed six
signed-zero differences for a mirrored identity. Preserving the original diagonal multiplication's
zero terms removed those differences generically. The absent-entity control returns zero values.

Local evidence is under `D:/aaemu-runtime/research`: `entity-direction-native-oracle.py`,
`entity-direction-native-controls.json`, and `entity-direction-comparison.json`. The standalone
managed comparison is under `D:/aaemu-runtime/entity-orientation-probe`; focused test output is
`D:/aaemu-runtime/entity-orientation-tests.log`. Original assets and local oracle artifacts are
not repository inputs.
