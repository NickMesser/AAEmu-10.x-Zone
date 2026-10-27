# Upstream licensing statements

This extraction preserves the original `LICENSE`, `LICENSE.GPL` and `LICENSE.MIT` files from the upstream AAEmu development snapshot `bc8a9e85845e18a33f0f98d9d02757ed0bfce03b`. `LICENSE.GPL` contains GNU GPL version 3. `LICENSE.MIT` contains the MIT License and `Copyright (c) 2019 Rain0Ash`.

The upstream `LICENSE` says:

> The project is distributed under the GPL.
> Contributors of this project may use the GPL OR MIT license.

The upstream root README's licensing section says:

> This program is free software: you can redistribute it and/or modify
> it under the terms of the GNU Lesser General Public License as published by
> the Free Software Foundation, either version 3 of the License, or
> (at your option) any later version.
>
> This program is distributed in the hope that it will be useful,
> but WITHOUT ANY WARRANTY; without even the implied warranty of
> MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
> GNU Lesser General Public License for more details.

These statements are retained as provenance, not reconciled into a definitive grant. No new license is asserted by this extraction. Retain original notices, acknowledge [AAEmu's contributors](https://github.com/AAEmu/AAEmu/graphs/contributors), and obtain rights-holder clarification where needed. Game/engine modules and assets are external; their rights are not supplied by these source license files.

## Attributed external implementations

The extraction records the following existing source attributions:

- The upstream `AAEmu.Commons/Utils/Helper.cs` cites [a CheckInterval implementation on CyberForum](https://www.cyberforum.ru/blogs/529033/blog3833.html). It has no callers in the extracted projects and was excluded from this publication copy. The source comment alone does not establish a redistribution license for the cited material.
- `AAEmu.Commons/Utils/AAPak/AAPak.cs` derives from the GPLv3 [AAEmu-Packer](https://github.com/ZeromusXYZ/AAEmu-Packer) project by ZeromusXYZ. The original repository's AssemblyInfo notice reads `Copyright © 2019-2021, No Rights Reserved`; it is preserved here without interpreting it as a waiver. The existing source attribution remains intact. The archive reader also contains fixed game-format cryptographic constants; these are not account credentials. Their provenance and applicable redistribution terms still require review.
- The old byte-array AES method cited a Stack Overflow implementation. That method body has been replaced independently with .NET's one-shot `Aes.EncryptCbc` / `Aes.DecryptCbc` APIs, retaining zero-IV CBC, no padding and the existing catch/error contract. The old citation was removed from that replaced method. Independent NIST test vectors and error-contract tests cover the replacement. This replacement does not assert a legal conclusion about the rest of the archive reader.

This inventory records the attribution present in the source; it does not claim that these external origins have been independently cleared under any of the conflicting upstream license statements.
