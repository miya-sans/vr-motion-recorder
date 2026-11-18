# Documentation to-do

The user documentation was written while the new UI and parts of the export were still being built. It describes
features and workflows, not button positions. This list says what to check once those parts are final. Remove an
item when it is done; delete the file when the list is empty.

## 1. Export is changing (owner: whoever lands the export change)

The app will write **one Unity humanoid clip per take, body and face in one `.anim`,** by itself. Unity will no
longer be needed to produce it. Today `<name>_humanoid.anim` is made by the Unity package from the imported FBX.

Update, in this order:

- [ ] `docs/export-and-pipeline.md`
  - the note at the top ("This page is being updated"): remove it;
  - the **Files** table: the `<name>_humanoid.anim` row (the "Written by" column and its note), and whether
    `_body.anim`, `_face.anim` and the `.vrmr.json` sidecar are still written, optional, or gone;
  - "Humanoid or Generic in Unity": the measured numbers are for the clip the Unity package builds (key reduction by
    cubic fit, 0.9 mm mean, 35 MB for 39 s). Measure the app-written clip and replace them;
  - "App → Unity": the steps, and what the package still does on import.
- [ ] `docs/file-formats.md`: "Export manifests and sidecars" (manifest keys for the new clip; the sidecar's role).
- [ ] `integrations/unity/com.vrmr.motion-bridge/README.md`: "What it does on its own" and the menu table; add a
      `CHANGELOG.md` entry and raise `package.json`'s version together with the Blender add-on's.
- [ ] `integrations/blender/README.md` and `integrations/README.md` if **Send to Unity** changes.
- [ ] `README.md`: the Export bullets under Features and step 3 and 4 of the quick start.

## 2. After the UI is final

Names of commands and settings in the docs are the feature's names from the previous UI. Check each against the
final labels, and add where to find it only if that is stable.

- [ ] `README.md`
  - the screenshot placeholder under the first paragraph (one image of the main window; add more to
    `docs/images/` and link them from the docs if useful);
  - "Download and first run": the first-run steps, the name of the release zip, the note about the Windows warning
    (drop it if releases get signed);
  - "Quick start": record button, **F9**, `VRMR/Record`, **I** / **O** / **Del**, the export choices.
- [ ] `docs/how-it-works.md`: recording controls and their defaults; the view toggles and device list in "The live
      view"; *Include VRChat jumps*; the tracking-loss commands (keep as recorded, mark as lost); where the
      stretches of an avatar resize are listed.
- [ ] `docs/calibration.md`: "the take's inspector (Calibration section)" and "Settings → Calibration defaults";
      **Capture now**, **Save to library**, **Delete**; the two switches for watching and automatic linking; the
      labels in the settings table; how the timeline shows calibrations, setting changes, jumps and calibration
      mode; the calibration list's match marks and warnings; the result summary after a solve.
- [ ] `docs/avatars.md`: **Add avatar…**; where the library and the default entry are set; how the base model is
      picked while wearing an avatar; **Export avatar FBX with its face shapes**; hiding meshes; the mannequin's
      description (check it against the shipped one).
- [ ] `docs/scenes.md`: the pose tool's modes and names; the re-record flow (arming, pre-roll, count-in); **Find
      best cut** and **Best entry**; modifiers' names; the key table (must equal the map in `docs/ui-design.md`).
- [ ] `docs/export-and-pipeline.md`: the export options' names; **Origin**, **Turn**, **Reset** and the placement
      gizmo; "replay face into VRChat".
- [ ] `docs/troubleshooting.md`: setting names ("source settings", the height setting, *Hang the avatar from the
      headset at calibration*); add entries for problems the first-run setup now explains itself.
- [ ] `docs/accuracy.md`: "Reproduce it on your own takes" (how the body source is switched, following an in-world
      HUMR button).
- [ ] `.github/ISSUE_TEMPLATE/bug.yml`: "Shown in Settings → About" for the app version.

## 3. Facts to confirm

- [ ] `docs/avatars.md`, "Adding a base model you own": written from the previous UI's import (package plus
      BlendShare add-on). Confirm the flow after the avatar library UI is rebuilt.
- [ ] `docs/file-formats.md`: `settings.json` is listed without its keys. If the settings get a schema version and
      migrations, describe them, and the `--data <folder>` layout (`AppData`, `Documents`).
- [ ] `SECURITY.md`, "What the app exposes": the table was read from `src/Vrmr.Capture/Sources/Osc.cs` and
      `OscQuery.cs`. Update it when a source or a port changes.
- [ ] `THIRD_PARTY_NOTICES.md`, section 5: re-check when `Directory.Packages.props` changes. The Inter typeface is
      under the SIL Open Font License 1.1, which asks for its licence text to travel with the font: add the text
      to the release zip (`licenses/Inter/OFL.txt`), since the NuGet package does not ship it.
- [ ] `docs/accuracy.md`: the measurement harness is not in the repository. If it is added (for example as a
      `vrmr` command), replace the paragraph that says so with how to run it.
- [ ] The integrations share one version (1.4.0, the Blender add-on's). Decide whether they follow the app's
      version from 1.0 on.

## 4. Not verified, because it could not be run here

- [ ] `.github/workflows/ci.yml`, job `linux`: no Linux machine was available. Known Windows-only code in the
      projects it builds is listed in the hand-over notes; the build should pass, the tests are expected to.
- [ ] `.github/workflows/release.yml`: runs only on a tag. Try it on a throw-away tag in a fork or a private copy
      before the first real release.
- [ ] `.github/ISSUE_TEMPLATE/config.yml` links to GitHub Discussions: enable Discussions for the repository, or
      remove that link.
