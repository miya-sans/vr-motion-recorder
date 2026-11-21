# Troubleshooting

Logs are kept in `%APPDATA%\VRMotionRecorder\logs`. Each run writes a separate `vrmr-<UTC timestamp>-<process ID>.log` file, preserving earlier runs. Settings → About → Open log opens the current run's file. Each line starts with an ISO 8601 UTC timestamp and a bracketed level, for example `2026-11-19T22:05:12.123Z [INF] VR Motion Recorder starting`.

## Nothing arrives from VRChat

- Switch OSC on in VRChat: Action Menu → Options → OSC → Enabled.
- If VRChat was started first, switch VRChat's OSC off and on once, or start the recorder before VRChat. VRChat then finds the recorder through OSCQuery and sends to it alongside other OSC apps.
- A firewall that blocks local multicast (mDNS, UDP 5353) stops OSCQuery discovery. *Also listen on UDP 9001* (Settings → Sources, off by default) then lets the app use VRChat's default port while VRChat runs and has not found the app for 30 seconds. Leave it off with face tracking apps such as Cymple, which own port 9001.

## No face tracking in the take

- The face is recorded from the avatar parameters VRChat echoes (`FT/v2/*`). Shapes the avatar does not have are not echoed.
- If VRChat treats the avatar as hardware full-face tracking, the face is sent as one binary message instead of parameters, and nothing reaches the recorder. Use the avatar's parameter-driven face tracking.
- Check that the avatar's face moves in VRChat at all: the recorder hears only what VRChat applies.

## The face plays, but not on my avatar's face

The take holds the parameters; showing them needs the avatar's face-tracking shapes. Add the base model together with its face-tracking add-on to the avatar library, or link the Unity project that has them. See [avatars.md](avatars.md).

## SteamVR devices do not show up

- The app connects to a running SteamVR and never starts it. Start SteamVR first, or leave the app open: it keeps looking.
- Check that SteamVR devices are switched on in the app's source settings.

## No finger tracking

Finger data needs SteamVR to load the app's input definition (a small `actions.json` that the app writes next to its program file, or into `%ProgramData%\VRMotionRecorder\steamvr` if that folder cannot be written). If the log says *SteamVR did not load the input manifest*:

- Start the app from Explorer or a shortcut, not from inside another program. A program installed as a Windows app package gives its child processes a private copy of AppData, at a path SteamVR cannot see.
- Unzip the app into a normal folder you can write to, not into a protected location.

Without finger data the hands use VRChat's gestures.

## The body is solved without my hip (or another) tracker

- A tracker that is not tracking at the calibration moment is not used. Calibrate again in VRChat with every tracker visible, or pick another calibration for the take.
- Look at the tracker roles of the take. Roles are found automatically; set one by hand if it is wrong, or set the roles in SteamVR's "Manage Trackers", which the app follows.
- A calibration from another SteamVR session, or from hours earlier, may no longer fit: straps move. The calibration list warns about both.

See [calibration.md](calibration.md).

## The avatar is not recognised, or the wrong body is shown

Public avatars and renamed edits carry no hint of their base model. Pick the base model while you wear the avatar; the choice is remembered for that avatar. For an existing take, choose the avatar in the take's settings. See [avatars.md](avatars.md).

## The feet are below the floor, or the knees are bent while I stand

That is the calibration's scale. By default your size is VRChat's User Real Height; if that value is wrong in VRChat, it is wrong here in the same way. Change the height setting for the take, or correct it in VRChat and calibrate again. With *Hang the avatar from the headset at calibration* on (the default, as VRChat does), a scaled headset below the avatar's eyes puts the feet under the floor rather than bending the knees.

## VRChat's own pose is missing from a take

VRChat's bones can only be recorded in a world that has a HUMR recorder, with VRChat's logging set to Full (VRChat's debug settings). Everywhere else the body is solved from the devices, which is the normal case.

## Windows warns when I start the app

The download is not code-signed. Windows shows a warning for programs it has not seen often: choose *More info → Run anyway*. If you prefer, build the app from source (see the [README](../README.md)).

## build.cmd says VRMotionRecorder.exe is in use

The app is still running, so the new build cannot replace it. Close the app and run `build.cmd` again.

## Still stuck

Open a [bug report](https://github.com/miya-sans/vr-motion-recorder/issues/new/choose). Read your log before you attach it: it can contain folder paths with your Windows user name, avatar names and world names.
