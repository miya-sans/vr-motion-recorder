# Current action

- [ ] Make SteamVR the recorder's only required external runtime, with optional integration modules for VRChat, HUMR, Blender, Unity, Basis, Resonite and Chillout.

The core owns device capture, avatar selection, tracker assignment, calibration, recording, review and generic export. Integration modules add platform-specific data sources and workflows.

Complete this action when the core workflow works with all integration modules disabled and their applications absent, and each enabled module can report its own availability without blocking core recording.

[Implementation checklist](TODO.md#steamvr-core-and-optional-integrations)
