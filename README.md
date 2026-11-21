<p align="center">
  <img src="assets/icon/vrmr-512.png" width="128" height="128" alt="VR Motion Recorder icon">
</p>

# VR Motion Recorder

VR Motion Recorder is a desktop motion capture tool for SteamVR. It records raw headset, controller and tracker poses, solves the motion on a humanoid skeleton, and exports animation for Blender and Unity.

The first milestone is recording with **only SteamVR running**. You should be able to choose the built-in mannequin or your own avatar, calibrate, record, then reopen and export the take with VRChat closed. SteamVR capture and solving already exist. The current desktop setup still assumes VRChat for parts of avatar selection and calibration, and the standalone workflow still needs testing on real hardware.

The take format can change during this foundation work. A future build may be unable to open takes from an earlier build. See [Project status](docs/dev/STATUS.md) for the current action, and the [development checklist](docs/dev/TODO.md) for pending work and previous validation.

## Features

Full list: [features.md](docs/features.md)

- Capture SteamVR headset, controller, tracker and finger motion.
- Solve motion on a humanoid avatar and edit takes while preserving the raw recording.
- Export animation to Blender or Unity.
- Capture face parameters through VRChat OSC or the avatar's solved pose in worlds with HUMR.

## Installation

### Requirements

- **Windows 10 or 11.**
- **SteamVR** for live body capture. Start it before recording. A headset and controllers provide head and hand motion. Additional trackers and supported finger tracking supply more input.
- **An avatar you own** (optional): add its base model to see and export your own avatar. A plain mannequin is built in.

### Download and first run

1. Download `VRMotionRecorder-<version>-win-x64.zip` from the [releases page](https://github.com/miya-sans/vr-motion-recorder/releases).
2. Unzip it anywhere and start `VRMotionRecorder.exe`. There is no installer and nothing else to install. Windows may warn about an app it does not know yet; choose *More info → Run anyway*.
3. The first run walks you through a short setup: what it found (SteamVR, VRChat, Blender, Unity projects), where to keep recordings, and which avatar to use. Every step can be skipped and changed later in Settings.

## Usage

Full guide: [usage.md](docs/usage.md)

1. Start SteamVR and check your devices in the live view.
2. Press **F9** to start and stop recording.
3. Open the take to review or trim it.
4. Export an FBX or Unity animation clip.

## Building

You need [Git](https://git-scm.com) and the [.NET SDK 10](https://dotnet.microsoft.com/download) or later:

```
git clone https://github.com/miya-sans/vr-motion-recorder
cd vr-motion-recorder
build.cmd
```

`build.cmd` puts the app in `dist\VRMotionRecorder.exe`. Pin it to the taskbar or make a shortcut to it. To update, close the app, then run `git pull` and `build.cmd` again: the pinned exe is replaced in place. It needs the .NET 10 runtime, which the SDK installs.

`build.cmd test` also builds and tests the whole solution first. To run straight from the source instead, use `dotnet run --project src/Vrmr.App -c Release`.

[CONTRIBUTING.md](CONTRIBUTING.md) covers tests, the project layout and the rules for contributions.

## Privacy

- **Everything stays on your PC.** Takes, scenes, settings and your avatar files are local files. Nothing is uploaded.
- **No telemetry, no account, no update checks.** The app makes no connection to the internet.
- **It only reads from VRChat and SteamVR.** It sends no input and writes none of their settings. The one thing it can send is the recorded face back to VRChat for a replay, and only when you switch that on.
- **On your network** it listens for VRChat's OSC data on your own computer, and announces that OSC endpoint on the local network so VRChat can find it (this is how OSCQuery works). Details are in [SECURITY.md](SECURITY.md).
- **Takes are personal.** A take holds your motion, your avatar's id and name, the world, your VRChat display name and your devices' serial numbers. Share one only if you are comfortable with that.

## Documentation

| Guide                                                                    | Contents                                                          |
| ------------------------------------------------------------------------ | ----------------------------------------------------------------- |
| [Features](docs/features.md)                                             | Capture, solving, editing and integrations.                       |
| [Usage](docs/usage.md)                                                   | Recording controls, reviewing takes and exporting animation.      |
| [Calibration](docs/calibration.md)                                       | VRChat-style calibration, every setting, the calibration library. |
| [Avatars](docs/avatars.md)                                               | The avatar library, adding a base model you own, the face.        |
| [Scenes](docs/scenes.md)                                                 | The scene editor: tracks, transitions, keyframes, re-recording.   |
| [Export and pipeline](docs/export-and-pipeline.md)                       | Export formats, Blender and Unity workflows.                      |
| [File formats](docs/file-formats.md)                                     | `.vrmr`, `.vrmscene`, manifests and sidecars.                     |
| [Accuracy](docs/accuracy.md)                                             | How the solve was measured against VRChat, and the results.       |
| [Troubleshooting](docs/troubleshooting.md)                               | No data, no face, no fingers, an avatar that is not recognised.   |
| [Architecture](docs/architecture.md), [UI design](docs/dev/ui-design.md) | Data flow, body solving, tracking repairs and desktop UI design.  |

## Contributing and support

Bug reports, device reports and feature requests go through the [issue forms](https://github.com/miya-sans/vr-motion-recorder/issues/new/choose). The solve was measured on one setup: reports from other headsets, controllers and trackers are especially welcome. Please read the [code of conduct](CODE_OF_CONDUCT.md), and report security problems privately as [SECURITY.md](SECURITY.md) says.

## Credits

- Parts of the body IK solver are derived from MIT-licensed software; the notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- The arm, spine and knee behaviour is matched to VRChat's IK 2.0 by measurement on recorded takes; no third-party IK source code is included.
- VRChat's own pose, where a take has it, comes through the log format of DrSakuu's [Hyakuashi Udon Motion Recorder](https://github.com/DrSakuu/HyakuashiUdonMotionRecorder) (MIT).
- Built with [.NET](https://dotnet.microsoft.com), [Avalonia](https://avaloniaui.net) and the other packages listed in the notices.
- The app icon is original to this project (`tools/make_icon.py` draws it) and MIT licensed like the rest.

VR Motion Recorder is a community project. It is not affiliated with or endorsed by VRChat, Valve, Unity or the Blender Foundation.

## Licence

[MIT](LICENSE). Copyright (c) 2026 miya-sans and VR Motion Recorder contributors.
