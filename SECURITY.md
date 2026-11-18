# Security and privacy

## Sharing a recording

A SteamVR take (`.vrmr`) can contain your headset, controller and tracker movements, device serial numbers, and the layout of your play area. Trimming or cutting a take changes playback and leaves the original samples in the file. Treat a trimmed take as the full recording when deciding whether to share it.

When captured, VRChat data can also include your display name, world name, avatar ID, face parameters and the avatar motion recorded by HUMR. For a bug report, a short test recording on the built-in mannequin is usually easier to share. Review any log excerpt for local paths and identifiers before posting it. See [File formats](docs/file-formats.md) for details.

## Live connections

The recorder reads device poses from SteamVR. With VRChat OSC enabled, it listens for messages on the same computer. Enabling OSCQuery also uses multicast discovery on the local network and lets the recorder query the discovered service for avatar parameters. Face replay, when enabled, sends recorded parameters to VRChat on the same computer.

To review or export an existing take with live sources disabled, start `VRMotionRecorder.exe --no-capture`.

## Reporting a security issue

Contact a project maintainer privately if you find that opening a take, importing an avatar or enabling a live source exposes data or writes files unexpectedly. If GitHub offers **Report a vulnerability** in the repository's Security tab, you can use that channel. Include the commit or build, the steps you took and what you observed. A minimal example with synthetic data lets the maintainer investigate without needing your personal recordings or purchased avatar files.
