## What this changes

<!-- What the change does and why it was needed. Link the issue if there is one. -->

## How it was checked

<!-- Tests you ran, takes you tried it on (describe them, do not attach them), numbers before and after. -->

## Checklist

- [ ] **No personal or licensed data.** No takes, scenes, avatar files, exports, screenshots that show a paid avatar,
      and no paths, names, ids or serial numbers from my own machine.
- [ ] **The motion is unchanged**, or the change is meant to alter it. `vrmr dump` before and after, compared with
      `python tools/compare_dumps.py`, says `IDENTICAL`; if the motion is meant to change, this pull request says
      what changed, by how much, and how it was measured.
- [ ] **The app still only reads from VRChat and SteamVR.** It sends no input, takes none of VRChat's ports and
      writes none of their settings.
- [ ] **It stays light.** No per-frame allocations in capture, solving or drawing, nothing slow on the UI thread,
      no timer that only redraws.
- [ ] **Third-party code keeps its notice** and is listed in `THIRD_PARTY_NOTICES.md`.
- [ ] `dotnet build VRMotionRecorder.slnx -c Release` and `dotnet test --solution VRMotionRecorder.slnx -c Release` pass.
- [ ] Public types and members I added have a one-line `///` summary, and the logic I added has tests.
- [ ] Docs are updated where behaviour, a file format or a setting changed.

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the details behind each point.
