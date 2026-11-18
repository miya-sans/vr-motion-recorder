# Security policy

## Reporting a problem

Please report security problems **privately**, not in a public issue:

- use [Report a vulnerability](https://github.com/miya-sans/vrc-motion-recorder/security/advisories/new) on
  GitHub (Security → Advisories → Report a vulnerability).

Say what you found, how to reproduce it, which version or commit you used, and what you think the impact is. A
proof of concept helps. Please do not include other people's personal data, and do not attach takes or avatar
files unless they are needed and you are comfortable sharing them.

This is a volunteer project. You can expect a first answer within about a week, and a fix or a plan as soon as the
problem is understood. You will be credited in the release notes unless you prefer not to be.

Only the latest release and the `main` branch receive fixes.

## What the app exposes

The app is a desktop program for one user. It needs no account and makes no connection to the internet. While it
runs with capture switched on, it opens these local endpoints:

| Endpoint | Bound to | Purpose |
|---|---|---|
| UDP, a free port chosen at start | 127.0.0.1 | Receives VRChat's OSC messages. Advertised through OSCQuery. |
| UDP 9001 (when free; can be switched off) | 127.0.0.1 | Receives VRChat's OSC messages on VRChat's default output port. |
| TCP, a free port chosen at start | 127.0.0.1 | OSCQuery: a small read-only HTTP service that describes the OSC endpoint above. |
| UDP 5353 (mDNS, multicast) | all network interfaces | Announces the OSCQuery service and finds VRChat's. This is the only traffic that leaves the computer: service names and port numbers on the local network, never recorded data. |

It also makes HTTP requests to the VRChat OSCQuery service it found through mDNS, to read the current avatar's
parameters. That is normally the VRChat on the same computer; a VRChat client elsewhere on the local network that
answers the discovery is treated the same way. Only while "replay face into VRChat" is switched on, it sends OSC
messages to VRChat's input port on 127.0.0.1.

Started with `--no-capture`, the app opens none of these.

## What counts

In scope, for example:

- a crafted OSC message, OSCQuery request or mDNS packet that crashes the app, makes it write outside its own
  folders, or runs code;
- a crafted take (`.vrmr`), scene (`.vrmscene`), avatar package (`.unitypackage`, `.zip`), FBX, BlendShare asset or
  JSON file that does the same when opened or imported. Packages are read in memory and nothing in them is meant
  to be executed: anything that gets around that is a vulnerability;
- a path in a scene, manifest or package that escapes the folder it should stay in;
- the Blender add-on or the Unity package writing outside the project folder you chose, or running code from an
  imported file;
- anything that sends recorded data, device serial numbers or avatar files off the computer;
- a problem in the release workflow or in a dependency that affects the published downloads.

Out of scope:

- problems that need an attacker who already controls your Windows account or your PC;
- VRChat, SteamVR, Unity, Blender and face-tracking software themselves (report those to their makers);
- the fact that takes and calibration snapshots contain personal data (avatar ids, world names, display name,
  device serial numbers). That is documented in [docs/file-formats.md](docs/file-formats.md); be careful what you
  share.

## Good practice for users

- Download releases only from this repository's releases page.
- Open takes, scenes and avatar packages only from people you trust, as you would any file.
- Read your log file before you post it: it can contain folder paths with your Windows user name.
