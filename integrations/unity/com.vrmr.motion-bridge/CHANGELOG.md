# Changelog

All notable changes to this package are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0]

First release as a Unity package. The editor scripts are the same as the loose-script version that shipped with the
app before; they keep their ids, so existing Timelines and references stay valid.

### Added

- `package.json`: install by Git URL or from a tarball. Requires Unity 2022.3 and installs Timeline with it.
- This changelog, a README and the licence file.

### Changed

- The scripts moved from `Assets/VRMotionBridge/Editor` into the package. Delete the old folder before installing.

### Included features

- FBX clips with a `.vrmr.json` next to them are imported as Humanoid, from the avatar's own model when it is in
  the project, otherwise from the bone mapping and T-pose in the file.
- `<clip>_humanoid.anim` is written for every such FBX.
- Clips sent from Blender get face, parameter and camera clips.
- Timelines from scenes (Humanoid, or exact pose on Generic Animators) and from Blender bundles.
- Make Humanoid Clip and Export Avatar Skeleton.
