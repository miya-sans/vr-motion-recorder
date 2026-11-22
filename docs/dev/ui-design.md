# UI design

The app is a tool people use with a headset pushed up on their forehead, between takes. The design goal is a calm window where the 3D view is the content, the next action is obvious, and nothing needs explaining twice.

## Principles

1. **Content first.** The 3D view and the timeline get the space. Panels are quiet and can be collapsed.
2. **One accent.** Blue marks the selection and the primary action; red is only for recording and for errors.
3. **Progressive disclosure.** Each panel shows the few controls people use every time. The rest sits behind an "Advanced" expander, closed by default, and remembered once opened.
4. **Direct manipulation.** Drag in the view, on the timeline and on values. Every drag is one undo step.
5. **No dead ends.** An empty list says what to do next and offers the button. A failed action says what went wrong in plain words and what to try.
6. **No interruptions.** Routine results appear as a short message in the status bar or a toast, never a dialog. Dialogs are for choices that cannot be undone (delete) and for file pickers.
7. **Keyboard complete.** Every command has a shortcut or is reachable with Tab and Enter. Tooltips show shortcuts.
8. **Says what it is doing.** A solve, an export or an avatar load shows progress where its result will appear.

## Window layout

- **Top bar.** Settings and Help are on the left. The Takes | Scene switch appears when the Scene workspace is available. Source status pills sit on the right; each opens a popover with details and, when needed, a suggested fix.
- **Library** (left). The Live toggle sits above the take library. Takes are listed newest first with name, date, length and badges for their contents. The Scene workspace adds the scenes list above the takes; takes are dragged onto tracks.
- **3D view** (centre). The view shows the open take, live devices or the Scene workspace. When the library or inspector is hidden, a button at the top of the view brings it back (Ctrl+1 for the library, Ctrl+2 for the inspector).
- **Transport and timeline** (bottom). In Takes, the transport changes with the mode: reviewing a take shows its playback controls; Live or recording shows Record, a recording countdown selector, Calibrate, a calibration countdown selector and the age of the last calibration. Calibration can use VRChat sync or a manual countdown. The Scene workspace has its own transport. The timeline can be resized or hidden with Ctrl+3; reopening it restores its previous height. In take review its timecode is shown with the frame number.
- **Inspector** (right). Shows controls for the selected take or Scene item and has a hide button. In Takes it contains take details, calibration, trackers, placement, face and export controls; Live shows device status.
- **Settings** opens as a page over the workspace: Sources, Recording, Folders, Avatars (the avatar library), Calibration defaults, View and About.
- **First run.** A short guided setup: what was detected (SteamVR, VRChat, Blender, Unity projects), where to keep recordings, and the avatar to solve onto (import one, or use the built-in mannequin). Every step can be skipped and reopened later from Settings.

Minimum window 1100 × 700.

## Visual language

- **Theme.** Dark by default, with a light variant from the same tokens. Colours come from `Themes/Tokens.axaml` only; no colour literals in views.
- **Colour tokens (dark).** Background `#121317`, surface `#1A1B21`, raised surface `#21232A`, border `#30323C`, text `#E4E6EC`, secondary text `#9498A6`, faint text `#646876`, accent `#6EA8FF`, accent muted `#34507F`, record `#F04848`, success `#56CE84`, warning `#F0BA50`. Left limbs `#60A0FF`, right limbs `#FF7474`.
- **Type.** Inter. 13 px body, 12 px secondary, 11 px captions, 15 px semibold section titles. Numbers that change while playing (time, frame) use tabular figures so they do not jitter.
- **Spacing.** A 4 px grid: 4, 8, 12, 16, 24. Controls are 28 px high (32 px in the top bar). Corners 6 px on controls, 8 px on panels and popovers.
- **Icons.** One outline icon set, 16 px, as vector geometry in `Themes/Icons.axaml`. An icon-only button always has a tooltip and an accessible name.
- **Motion.** 120 ms ease-out for popovers and expanders. Nothing animates while recording.

## Controls

Reusable controls live in `Controls/` and are used everywhere instead of ad-hoc layouts:

- `Section` – a titled group that folds; remembers its state per id.
- `FieldRow` – label left, editor right, optional unit and reset-to-default.
- `NumberField` – type a value, drag to scrub, arrow keys step; Shift is fine, Ctrl is coarse; one undo step per drag.
- `SegmentedControl`, `ToggleChip`, `StatusPill`, `IconButton`.
- `EmptyState` – icon, one sentence, one button.
- `Toast` and the status bar message queue.
- `TimelineControl`, `NlaControl` – custom-drawn, virtualised, and redrawn only when their data or the playhead moves.

## Behaviour that must not change

Everything the previous UI could do stays possible. Keep the keyboard map: Space play/pause, ←/→ step a frame, Home/End, I/O trim, Del cut, Ctrl+S save, Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y undo and redo; in scenes K cut, T transition, M mute, Shift+D duplicate, F frame all, Shift+M marker; in the pose tool I / Shift+I / Alt+I keys, M mirror, Ctrl+C / Ctrl+V pose, `,` `.` previous and next key, W move, E turn, H device on/off. The mouse wheel scrolls panels and never changes a value.

The [roadmap](TODO.md#scene-editing) tracks missing desktop features. The [development checklist](TODO.md) records validation and outstanding window and hardware checks. Verify a migrated feature on a real take before describing it as fully checked.

## Writing

Sentence case. Verbs on buttons ("Export", "Add avatar…"). No jargon where a plain word exists: "IK solve" rather than solver names, "tracker" rather than device class numbers. Units are shown (cm, °, s). Tooltips explain what a control does to the result, in one or two sentences.
