# Third-party notices

VR Motion Recorder is released under the MIT License (see [LICENSE](LICENSE)). It contains, reads or depends on the
work of others, listed here with their licences. If you add code or a dependency under another licence, add it to
this file and keep the notice in the file headers.

## 1. Body IK solver (`src/Vrmr.Fbik`)

Parts of the body IK solver in `src/Vrmr.Fbik` are derived from software released under the MIT License. The files
concerned carry a `Portions Copyright` header. The derived code was converted to plain C# (no game-engine
dependencies) and changed; the changes are part of this project and under its licence.

```text
MIT License

Copyright (c) 2024 MR LUKE B DOOLAN

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

A copy of this notice also lives next to the code, in [`src/Vrmr.Fbik/NOTICE.txt`](src/Vrmr.Fbik/NOTICE.txt), so it
travels with that folder.

## 2. Spine solver (`src/Vrmr.Fbik/Spine/RalivIKSpine.cs`)

This file carries its own MIT notice, quoted here as it stands in the file:

```text
MIT License

Copyright (c) 2026 Raliv and Gator Dragon Games

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 3. HUMR log format

In worlds that have a [Hyakuashi Udon Motion Recorder](https://github.com/DrSakuu/HyakuashiUdonMotionRecorder)
(HUMR, by DrSakuu, MIT License), VRChat writes the avatar's bone rotations into its log. VR Motion Recorder reads
that log format and can write a HUMR-compatible log on export. No HUMR code is included.

## 4. Behaviour matched to VRChat

The arm, spine and knee behaviour is matched to VRChat's IK 2.0 by measurement on recorded takes; no third-party IK
source code is included. See [docs/dev/accuracy.md](docs/dev/accuracy.md).

## 5. NuGet packages

Versions are pinned in [`Directory.Packages.props`](Directory.Packages.props). Licences are as declared in each
package's `.nuspec`. Only packages that a project references are listed.

Shipped with the app:

| Package | Licence | Copyright |
|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia), Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter (and the Avalonia platform packages they bring in) | MIT | © The AvaloniaUI Project |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | © .NET Foundation and Contributors |
| [LiteDB](https://github.com/litedb-org/LiteDB) | MIT | © Mauricio David |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT | © .NET Foundation and Contributors |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) and its native SQLite assets | Apache-2.0; SQLite is public domain | © Eric Sink; SQLite authors |
| [Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET) (with Silk.NET.Core, Silk.NET.Maths) | MIT | © .NET Foundation and Contributors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and its native assets | MIT | © Microsoft Corporation |
| HarfBuzzSharp and its native assets (through Avalonia) | MIT | © Microsoft Corporation |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) (through Avalonia) | MIT | © Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) (through Avalonia, Linux only) | MIT | © Tom Deseyn |
| Avalonia.Angle.Windows.Natives (through Avalonia) | BSD 3-Clause | © The ANGLE Project Authors |

Native libraries and fonts inside those packages have their own licences:

| Component | Comes with | Licence |
|---|---|---|
| Skia | SkiaSharp native assets | BSD 3-Clause, © Google LLC; further notices in the package's `THIRD-PARTY-NOTICES.txt` |
| HarfBuzz | HarfBuzzSharp native assets | MIT-style ("Old MIT"); notices in the package's `THIRD-PARTY-NOTICES.txt` |
| ANGLE | Avalonia.Angle.Windows.Natives | BSD 3-Clause, © The ANGLE Project Authors |
| Inter typeface | Avalonia.Fonts.Inter | SIL Open Font License 1.1, © The Inter Project Authors (<https://github.com/rsms/inter>) |

The self-contained download also contains the .NET runtime (MIT, © .NET Foundation and Contributors).

The release build copies the licence and notice files that these packages ship into a `licenses` folder of the
download (`.github/scripts/collect-licenses.ps1`).

Used for building and testing only (not shipped):

| Package | Licence | Copyright |
|---|---|---|
| [xunit.v3](https://github.com/xunit/xunit) | Apache-2.0 | © .NET Foundation |
| Microsoft.Testing.Platform (through xunit.v3) | MIT | © Microsoft Corporation |
| Avalonia.Headless | MIT | © The AvaloniaUI Project |

To print the full list, including indirect packages:

```
dotnet list VRMotionRecorder.slnx package --include-transitive
```

## 6. Programs and formats the app talks to

No code from these is included. The app reads their data or writes files they can open: VRChat (OSC, OSCQuery, its
log file), SteamVR (through the `openvr_api.dll` of your own SteamVR installation), VRCFaceTracking's avatar
parameters as VRChat echoes them, BlendShare assets, Unity (`.meta`, `.unitypackage`, `.anim`), FBX, and Blender
through the add-on in `integrations/blender`.

VRChat, SteamVR, Unity, Blender and the other names are trademarks of their owners. This project is not affiliated
with or endorsed by any of them.
