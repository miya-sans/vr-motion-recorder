# UI style

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

- **Top bar.** Settings and Help are on the left. Source status pills on the right open popovers with details and suggested fixes where applicable.
- **Library** (left). The Live toggle sits above the take library. Takes are listed newest first with name, date, length and badges for their contents.
- **3D view** (centre). The view shows the open take or live devices. When the library or inspector is hidden, a button at the top of the view brings it back. Use Ctrl+1 for the library and Ctrl+2 for the inspector.
- **Transport and timeline** (bottom). Take review shows playback controls, timecode and frame number. Live shows recording and calibration controls with countdown selectors and the last calibration age. Calibration can use VRChat sync or a manual countdown. Resize the timeline with its divider or toggle it with Ctrl+3. Reopening it restores the last height.
- **Inspector** (right). **Devices** shows live source and device status. **Take** contains take details, calibration, trackers, placement and face controls. **Export** shows export formats and options.
- **Settings** opens as a page over the workspace: Sources, Recording, Folders, Avatars (the avatar library), Calibration defaults, View and About.
- **First run.** A short guided setup: what was detected (SteamVR, VRChat, Blender, Unity projects), where to keep recordings, and the avatar to solve onto (import one, or use the built-in mannequin). Every step can be skipped and reopened later from Settings.

Minimum window 1100 × 700.

The desktop build currently opens in Takes. The Scene workspace is disabled, and the Record, View, Edit and Export tabs are documented separately for the `tab-ui-design` branch. [Tab UI design](tab-ui-design.md)

## Visual language

- **Theme.** Dark by default. Settings → View offers Dark, Light and System. Use the resources in `Themes/Tokens.axaml` for colours and other shared values.
- **Colour tokens (dark).** Background `#121317`, surface `#1A1B21`, raised surface `#21232A`, border `#30323C`, text `#E4E6EC`, secondary text `#9498A6`, faint text `#646876`, accent `#6EA8FF`, accent muted `#34507F`, record `#F04848`, success `#56CE84`, warning `#F0BA50`. Left limbs `#60A0FF`, right limbs `#FF7474`.
- **Type.** Inter. 13 px body, 12 px secondary, 11 px captions, 15 px semibold section titles. Numbers that change while playing (time, frame) use tabular figures so they do not jitter.
- **Spacing.** A 4 px grid: 4, 8, 12, 16, 24. Controls are 28 px high (32 px in the top bar). Corners 6 px on controls, 8 px on panels and popovers.
- **Icons.** One outline icon set, 16 px, as vector geometry in `Themes/Icons.axaml`. An icon-only button always has a tooltip and an accessible name.
- **Motion.** `MotionFast` defines the 120 ms duration for short transitions. Keep decorative motion out of the recording workflow.

## Controls

Reusable controls live in `Controls/` and are used everywhere instead of ad-hoc layouts:

- `Section` groups each panel into collapsible sections. Set a `StateKey` to remember whether each section is open. Put rarely used settings in a nested **Advanced** section, closed by default.
- `FieldRow` shows one setting with its label on the left and editor on the right. Add a unit or reset action where applicable.
- `NumberField` shows numbers with their unit and supports typing, dragging to scrub, and stepping with the arrow keys. Shift makes finer adjustments and Ctrl makes coarser ones. A drag is one undo step.
- `SegmentedControl`, `ToggleChip`, `StatusPill`, `IconButton`.
- `EmptyState` – icon, one sentence, one button.
- `Toast` and the status bar message queue.
- `TimelineControl` draws the take timeline and playhead.

## Keyboard and mouse

- **Window.** F1 toggles Help. Ctrl+, opens Settings. Escape closes the top page. Ctrl+1, Ctrl+2 and Ctrl+3 toggle the library, inspector and timeline.
- **Library and take.** Ctrl+O opens a take file, Ctrl+F focuses library search, F2 focuses the open take’s name, and Ctrl+S saves. Ctrl+Z undoes. Ctrl+Shift+Z and Ctrl+Y redo.
- **Playback and edits.** Space plays or pauses, ← and → step a frame, Home and End seek to the start or end, I and O set the trim bounds, and Del cuts the selected range. Focused controls retain their navigation keys.
- **Viewport.** F frames the take. Numpad 1, 3, 7 and 5 select front, side, top and perspective views. With the placement gizmo active, W or G selects move, E or R selects rotate, and Escape closes the gizmo.
- **Recording.** F9 is the default global recording hotkey. Change it in Settings → Recording.
- **Mouse wheel.** Scroll panels without changing field values. In the 3D view, the wheel zooms.

The main window handles desktop shortcuts. The development checklist tracks outstanding window and hardware checks. [Main window shortcuts](../../src/Vrmr.App/Views/MainWindow.axaml.cs) · [Development checklist](TODO.md)

## Writing

Sentence case. Verbs on buttons ("Export", "Add avatar…"). No jargon where a plain word exists: "IK solve" rather than solver names, "tracker" rather than device class numbers. Units are shown (cm, °, s). Tooltips explain what a control does to the result, in one or two sentences.
