# Protobot Rebuilt Context Map

This is the quick orientation document for both human contributors and coding agents. It describes the current repository rather than the historical release documented in parts of the README.

## Project at a glance

- Unity editor: `6000.6.6f1` (see `ProjectSettings/ProjectVersion.txt`).
- Active scene: `Assets/Scenes/Main.unity`.
- Rendering: Universal Render Pipeline (URP). The main 3D camera is the Base camera; `UICamera` is stacked as an Overlay camera.
- Primary macOS development build: `siliconbuildtest6.6urp.app`.
- Source namespace is mostly `Protobot`; older scripts in the project may still use the global namespace.

## Runtime architecture

| Area | Primary locations | Responsibility |
| --- | --- | --- |
| Input | `Assets/Scripts/Input`, `Assets/Prefabs/Keybindings.prefab` | Default bindings, rebinding, mouse casting, and noclip capture. `InputEvent` is the common abstraction. |
| Selection | `Assets/Scripts/Selection` | Single, hole, box, and multi-selection. `SelectionManager.current` is the authoritative active selection. A `MultiSelection` owns a `MultiPivot`. |
| Movement | `Assets/Scripts/Movement`, `Assets/Scripts/Parenting/Pivot.cs` | Moves objects and rigid sets through temporary or selection pivots. Preserve world transforms when changing parents. |
| Tools | `Assets/Scripts/Tools` | Transform, move/pivot, color, Connect, and their handles/options. Toolbar buttons belong to one `ToggleGroup`. |
| Placement | `Assets/Scripts/Placement` | Preview and final placement of parts. Free placement uses the shared transform snapping rules. |
| Snapping | `Assets/Scripts/Tools/TransformSnapping.cs` | Shared 0.5-unit movement and 15-degree rotation snapping. Holding Snap Bypass temporarily disables it. |
| Undo/redo | `Assets/Scripts/StateSystem` | Capture affected `SavedObject` transforms before a change, create a state, then capture the resulting transforms. |
| Outlines | `Assets/Scripts/Outlining`, `Assets/Universal RP_Renderer.asset` | URP renderer feature and selection outline behavior. |
| Editor repair/setup | `Assets/Editor/UrpMaterialMigration.cs` | Idempotent project migration and scene/prefab wiring helpers used by batch-mode maintenance. |

## Core interaction flow

1. `MouseCast` resolves the collider under the pointer.
2. A selector creates an `ISelection` and sends it to `SelectionManager`.
3. Selection responses update outlines, object links, tool availability, and state behavior.
4. A movement tool operates on the selected object or `MultiPivot`.
5. `ObjectElement` snapshots provide undo/redo state around the mutation.

The Connect tool is slightly different: it freezes the body set when activated, captures a source hole/body pivot and a target hole/body pivot, then applies the same translation delta to the independent movement roots. It intentionally does not rotate parts.

## Default tool and camera bindings

| Binding | Action |
| --- | --- |
| `1` | Transform tool |
| `2` | Move/pivot tool |
| `3` | Color tool |
| `4` | Connect tool |
| Hold `Ctrl` / `Command` | Temporarily bypass snapping |
| Hold `Shift` | Noclip camera mode |
| Noclip `W/A/S/D` | Horizontal camera movement |
| Noclip `E/Q` | Camera up/down |

Bindings are rebindable in Preferences. While noclip mode is active, its movement keys are captured so unrelated bindings do not also fire.

## Connect tool contract

- Optional preselection defines the rigid set that will move.
- With no preselection, the source endpoint's body/connected group becomes the moving set.
- The source endpoint must belong to the moving set; the target must be outside it.
- A hole endpoint uses its live hole center. A body endpoint uses the `SavedObject` transform position.
- Multiple selected bodies retain their relative offsets and move through their existing `MultiPivot`.
- Direction/orientation is ignored. Only translation is applied.
- `Escape` exits the tool; completed operations participate in undo/redo.

## Important invariants and pitfalls

- Do not add a second parent pivot around an active `MultiPivot`; translate the existing pivot instead.
- Avoid moving both a parent and its child as separate roots or the child will receive the delta twice.
- Hole colliders live on the `HoleCollisions` layer and reference their owning part through `HoleData.part`.
- Placement-layer preview objects must not be treated as normal selectable targets.
- `Assets/Scenes/Main copy.unity` is not the active production scene.
- Unity-generated `.meta` files must accompany every new asset.
- Preserve unrelated work in the working tree. Build products and `Library` are generated artifacts, not source.

## Validation and build

Run a headless compile/import check:

```sh
/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /absolute/path/to/Protobot-Rebuilt \
  -logFile /tmp/protobot-compile.log
```

Build the macOS player:

```sh
/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /absolute/path/to/Protobot-Rebuilt \
  -buildTarget StandaloneOSX \
  -buildOSXUniversalPlayer /absolute/path/to/siliconbuildtest6.6urp.app \
  -logFile /tmp/protobot-build.log
```

After scene/prefab edits, check for C# errors, missing serialized references, runtime exceptions, and a clean `git diff --check`. Existing convex-mesh import warnings are known and are not compilation failures.
