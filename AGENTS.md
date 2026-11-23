# Repository Guidance

`CONTEXT_MAP.md` is the shared architecture, workflow, keybinding, and validation reference for this repository.

Before modifying the project, consult the relevant sections of `CONTEXT_MAP.md` for the subsystems involved. Do not read the entire document unless the task requires broad architectural understanding.

Keep the map current when a change moves an architectural entry point, changes an interaction contract, or adds a major subsystem.

Preserve unrelated working-tree changes and treat `Assets/Scenes/Main.unity` as the active production scene unless the task explicitly says otherwise.

Run validation relevant to the modified subsystems. Consult the validation commands in `CONTEXT_MAP.md` as needed rather than executing unrelated test suites.