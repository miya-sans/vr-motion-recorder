# Features

## Recording

- Records SteamVR device poses, tracking changes and finger motion from SteamVR Skeletal Input on supported controllers.
- Writes samples while recording and timestamps them on a shared clock with any optional VRChat inputs.

## The body solve

- A full-body IK solve on your avatar's own skeleton, from 3 points (head and hands) up to 11.
- Matched to VRChat's IK 2.0 by measurement: with 11-point tracking the bones are 1.7 cm from VRChat's own pose on average, with head, hands, hips and feet 2.1 cm. See [accuracy measurements](accuracy.md) for what that means.
- Follows what you did in VRChat: calibrations, spine lock and measurement changes, playspace resets.
- Repairs trackers that drop out, and finds which tracker is on which body part.
- Takes keep the raw data, so everything can be changed afterwards: calibration, avatar, tracker roles, settings.

## Avatars

- An avatar library for the base models you own; import straight from the avatar's package, no Unity needed.
- Face-tracking shapes from BlendShare add-ons are merged in, so the face plays on the real model.
- Solve any take onto any other humanoid avatar.

## Editing

- Trim, cut and place takes, with undo. Nothing changes the recording.
- Planned for the Avalonia app: a scene editor with tracks and strips, scored transitions, body-part layers, keyframes with an IK pose tool, re-recording, input editing and foot lock. The scene engine exists, but its desktop workspace is not yet available.

## Export and pipeline

- FBX with your avatar's meshes, textures and face shape keys; armature-only FBX; Unity clips for Generic and Humanoid rigs; whole scenes with several actors.
- Blender add-on: avatar, face, parameters and cameras in one import; retargeting; **Send to Unity**.
- Unity package: Humanoid set-up, clips and Timelines, ready for a render with physics.

## Optional VRChat integrations

- With OSC enabled in VRChat, the recorder can capture face tracking, avatar parameters and camera poses. When using VRChat full-body tracking, calibrate as usual. The avatar parameter `VRMR/Record` can start or stop a take.
- In worlds equipped with Hyakuashi Udon Motion Recorder (HUMR), the recorder can read VRChat's solved avatar motion from its log alongside the SteamVR device poses.
- Planned video player support will use song changes in the VRChat log to mark clips and play the reference video alongside the recorded motion.

## Who it is for

- **Performers** who want to record dances, acting or gestures with their SteamVR devices.
- **Animators** who would rather clean up a real performance in Blender than key everything by hand.
- **People who film in Unity** and want the take on their avatar with hair and cloth physics on top.

You do not need full-body tracking. Head and hands alone give an upper body; every tracker you add is used.
