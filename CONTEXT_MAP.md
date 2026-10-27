# Protobot Rebuilt Context Map

This is the quick orientation document for both human contributors and coding agents. It describes the current repository rather than the historical release documented in parts of the README.

## Project at a glance

- Unity editor: `6000.6.6f1` (see `ProjectSettings/ProjectVersion.txt`).
- Active scene: `Assets/Scenes/Main.unity`.
- Rendering: Universal Render Pipeline (URP). The main 3D camera is the Base camera; `UICamera` is stacked as an Overlay camera.
- Primary macOS development build: `build.app`.
- Source namespace is mostly `Protobot`; older scripts in the project may still use the global namespace.

## Runtime architecture

| Area | Primary locations | Responsibility |
| --- | --- | --- |
| Input | `Assets/Scripts/Input`, `Assets/Prefabs/Keybindings.prefab` | Default bindings, rebinding, mouse casting, and noclip capture. `InputEvent` is the common abstraction. |
| Selection | `Assets/Scripts/Selection` | Single, hole, box, and multi-selection. `SelectionManager.current` is the authoritative active selection. A `MultiSelection` owns a `MultiPivot`. |
| Movement | `Assets/Scripts/Movement`, `Assets/Scripts/Parenting/Pivot.cs` | Moves objects and rigid sets through temporary or selection pivots. Preserve world transforms when changing parents. |
| Automatic groups | `Assets/Scripts/Parenting/GroupManager.cs`, `ConnectingPart.cs`, `Assets/Scripts/HoleDetector.cs` | Live screw/shaft connections, detector ownership, and connected part sets. |
| Tools | `Assets/Scripts/Tools` | Transform, move/pivot, color, Connect, and their handles/options. Toolbar buttons belong to one `ToggleGroup`. |
| Keyboard transforms | `Assets/Scripts/Tools/KeyboardTransformTool.cs`, `TransformGesture.cs`, `TransformNumericInput.cs` | Smoothed modal grab/rotation, world-axis constraints from the starting transform, rigid selection roots, and confirmation/cancellation. Wired on Placement in Main.unity. |
| Part properties | `Assets/Scripts/Part Management/PartParameterEditing.cs`, `Assets/Scripts/UI/Add Menu UIs/AddPropertiesUI.cs` | Parameter stepping, pick-part placement, and undoable regeneration of a single placed part. The controller lives on Placement so hiding the add menu does not disable shortcuts. |
| Placement | `Assets/Scripts/Placement` | Preview and final placement of parts. Initial placement snaps ordinary parts onto hovered holes after the specialized hardware rules; free placement uses the shared transform snapping rules. |
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
| `G` | Grab selected parts in the camera plane |
| `R` | Rotate placed selections about the world axis closest to the camera's viewing direction; disabled during placement |
| `X` / `Y` / `Z` during grab/rotation | Constrain to a world axis through the starting position; repeat to restore free grab or rotation's initial primary axis |
| `H` during grab | Switch to hole snapping; press H again for free movement, or X/Y/Z for an axis |
| Click / `Enter` during selection grab/rotation | Confirm as one undoable operation |
| `Escape` / right-click during selection grab/rotation | Cancel and restore the starting transforms |
| `P` | Pick the selected part's type and properties into normal placement |
| `Up` / `Down` | Increase / decrease the first (top) property during placement or on a single selected part |
| `Right` / `Left` | Increase / decrease the second (bottom) property during placement or on a single selected part |
| Hold `Space` over a hole | Use the opposite face of that same hole; release to use the hovered face |
| `/` during G/R | Open relative numeric entry on the current axis; press again for a global value, then again to return to mouse control |
| `/` during unconstrained G | Enter a global `X, Y, Z` position directly; slash again returns to mouse control |
| `H` outside grab | Toggle grid visibility |
| `J` | Toggle holes only |
| Hold `Ctrl` / `Command` | Temporarily bypass snapping |
| Hold `Ctrl` / `Command` while clicking a part/hole | Select that part individually, bypassing its connected assembly; the scope persists after release |
| Hold Duplicate (default `Alt`) when starting an axis drag or `G` / `R` | Leave copies at the starting pose while transforming the selected parts |
| Hold `Shift` | Noclip camera mode |
| Noclip `W/A/S/D` | Horizontal camera movement |
| Noclip `E/Q` | Move along the camera's screen up/down directions, including camera pitch |

Bindings are rebindable in Preferences. While noclip mode is active, its movement keys are captured so unrelated bindings do not also fire.

During G/R, physical X/Y/Z take priority over every conflicting binding, including rebound Grab, Rotate, grid, Duplicate and Snap Bypass actions. Held conflicting actions stay suppressed until release. `Grab Hole Snap` is a separate rebindable input (default H), with a Preferences row; during grab it consumes conflicting inputs before their handlers run, including grid visibility. It is inactive during rotation and ordinary selection.

Grab hole snapping uses a selected hole face immediately, moving its connected/grouped set unless individually selected. For a multi-selection or body selection, H first accepts a source-hole click on the moving bodies. Hovering an outside hole then translates and rotates the rigid set so the source face meets the target face; Space uses the opposite target face. Target casts ignore moving colliders and reject sources outside the selection. Existing MultiPivots move directly. H again returns to free movement at the displayed pose. X/Y/Z and relative numeric entry always refer to the original grab anchor, including after H mode; selecting an axis projects back onto that original axis. The temporary source-hole pivot never replaces the operation's base position. Cancel restores the starting transforms; confirmation includes all mode changes in one undo step.

Automatic groups use symmetric live graph edges. A screw connects parts between its head and its furthest threaded hole. A shaft needs two distinct clamp bodies and includes only holes between the outermost clamps. Detector additions/removals are idempotent and enforce ownership; perpendicular holes cannot attach. Every hole change refreshes the fastener graph after the frame, replacing previous membership even when counts match. Disabling/deleting holes, detectors or fasteners releases occupancy and connections; re-enabled geometry is detected again by physics. Graph traversal excludes inactive/deleted parts and resets at runtime startup.

`ISelection.individual` records the Snap Bypass modifier when pointer selection occurs and follows hole selections, multi-selection membership, undo/redo and property regeneration. Selection links carry this scope to keyboard transforms, handle movement, duplication, Connect, outlines and move-tool previews; releasing the modifier does not silently re-expand the assembly.

Grab and rotation use the shared 0.5-unit / 15-degree snapping and Snap Bypass binding. Both ease toward their targets over 0.25 seconds with DOTween's standard easing, matching axis dragging. Confirmation lets the easing finish before capturing the exact final transform for undo; cancellation kills the tween and restores the starting pose. The shared rigid displacement is interpolated so multi-selection spacing stays fixed during rotation. Rotation starts on the nearest primary world axis (largest absolute camera-forward component), captured when R is pressed; it never rotates about an arbitrary camera axis. Placement rotation is deprecated: its old handler and implementation remain under `#if false`, and its legacy public entry points are inert and marked obsolete. The existing `Rotate Placement` binding ID is retained for saved preferences and appears as `Rotate Selection` in Preferences. Modal transforms capture unrelated input through settling and the confirmation frame and consume held inputs until release. Multi-selection transforms use the existing MultiPivot directly.

Axis dragging and keyboard transforms share `ObjectDuplicator.CreateCopies`, which clones each part once without cloning a selection pivot and gives copies their own materials. Keyboard transforms read the existing rebindable Duplicate modifier when the operation starts. Copies stay stationary; confirmation records their existence and the selected parts' transforms in one undo step. Cancellation or confirmation without a transform discards temporary copies. Transform and move/pivot `ToolToggle`s opt into `hideDuringKeyboardTransform`; their handle roots deactivate in LateUpdate while G/R captures input, including settling and the confirmation frame, then reactivate according to the retained toolbar selection.

## Transform feedback and part properties

- Mouse handle drags and G/R show the operation's snapped target distance in inches or angle in degrees above and to the right of the pointer. `TransformFeedback` scales font sizes, boxes and offsets together against a 1600×900 reference, with a minimum readable size; the G/R help panel also wraps to the available width. G with X/Y/Z draws a red/green/blue world-axis guide through the starting pivot. Set `KeyboardTransformTool.showConstraintAxis` to false on Placement to disable that guide.
- Slash numeric entry applies to G/R only. On an axis, slash cycles relative → global → mouse; unconstrained grab opens global `X, Y, Z` entry immediately. Numbers use decimal points, optional signs and inches/degrees. Relative values measure from the operation's starting pivot/orientation. Global movement sets the pivot's chosen world coordinate; global rotation replaces the selected world Euler component and preserves the other two components of the selection's initial orientation. Multi-selection uses its pivot as the orientation reference.
- Enter with valid numeric input confirms one undoable operation with the exact value, bypassing snapping. Typing pauses manual targeting; third slash or invalid Enter discards the text, rebases the pointer at the existing target and resumes mouse control without a jump. Escape/right-click still cancels the entire operation. `TransformNumericInput` receives text through the Input System, supports negative values, editing and paste, and captures transform shortcuts while typing.
- Selection Preferences rows use distinct 35-unit spacing, and the Preferences panel grows to fit them. `InteractionUpdatesSetup` lays out both existing and newly added shortcuts, including all four property arrows.
- `HoleData` reads its source collider transform directly. `HoleFace` retains its direction relative to the owning part and refreshes in Update/LateUpdate; handle followers run in LateUpdate. Selecting, moving, rotating and undoing a hole selection retain its live pivot and face orientation.
- Arrow shortcuts repeat while held, step lists in their displayed order, change numeric hole counts by 1 and other custom numbers by 0.5, and respect limits. Changing the first property normalizes dependent second-property options/limits. Text fields, rebinding, noclip, Connect and active transforms capture input before property shortcuts.
- Placement property changes refresh the preview mesh without restarting placement or resetting its mechanical alignment. P uses the selected part's saved ID to start ordinary repeated placement; it does not duplicate the part or its connected group. All five new shortcuts have Preferences rows and support rebinding.
- Initial placement uses `PartHolePlaceDisplacement` before the free-plane fallback. It reads the live hole collider under the pointer and honors Space for the opposite face. `PartGenerator.TryGetPlacementHole` supplies a primary or nearest local hole; plates and aluminum generators account for generated hole offsets, including even counts. The new part's mating hole face sits exactly on the target face, without grid quantization. Parts with no source hole place their origin at the target face. Attachment data refreshes with preview properties; hardware-specific placement retains priority.
- New parts reset to their own initial orientation when placement starts and when returning to the free plane. `PartData.flipPlacement` turns over specific variants (bevel gears, rack, hand crank, inner slide trunk, fixed-length rails, Vision/Potentiometer sensors, and both motors). Aluminum generators expose their own `flipPlacement` setting: channels and angles turn over, while the generated motion rail retains its original orientation. Fixed-length `RAILS` and generated `RAIL` are distinct part types and require separate coverage. `PartPlacementData` caches the corrected free orientation and mounting face together. Specialized hardware rules apply the same orientation correction.
- `PartData.placementHole` optionally chooses the screw mounting anchor independently of `primaryHole`. Both motors use a threaded mounting hole for initial placement and retain the shaft socket as their primary/mechanical hole and `Motor.shaftHole`; both mounting screws then align with the half-inch structural pattern. The potentiometer likewise uses a screw hole for placement while retaining its central shaft hole. Attachment holes must belong to the chosen variant; the 5.5W motor and high-strength block bearing reference their own geometry, keeping prefab display offsets out of runtime pivots.
- A single selected part exposes its properties in the existing panel. Property edits regenerate the part through its generator, retaining world pose, color and saved state. With a hole selected, the closest remaining local hole stays at the selected hole center. Regeneration rebuilds holes/colliders/detectors and saves the new parameter ID; physics discovers connections for the new geometry. Each change records old/new existence and selection as one undoable operation. These programmatic selection restorations bypass pointer selection conditions (including the UI guard), while ordinary selections retain their existing conditions. Multi-selection property editing is intentionally excluded.
- The legacy `Select Opposite Hole` binding ID remains compatible with saved preferences and is labeled `Opposite Hole Face (Hold)` in Preferences. Space flips the hovered face normal and chooses the other end of that hole, rather than searching for another hole.

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

Build the Apple Silicon macOS player as `build.app` (the helper explicitly selects ARM64 and restores the previous architecture setting):

```sh
/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /absolute/path/to/Protobot-Rebuilt \
  -buildTarget StandaloneOSX \
  -executeMethod ProtobotEditorTools.MacBuild.BuildAppleSilicon \
  -logFile /tmp/protobot-build.log
```

For local macOS use, refresh and verify the app's ad hoc signature after building (copied Unity libraries can retain invalid signatures after architecture stripping):

```sh
codesign --force --deep --sign - --preserve-metadata=identifier,entitlements,flags,runtime build.app
codesign --verify --deep --strict build.app
```

Build the 64-bit Windows Mono player into `Builds/Windows-Mono/Protobot.exe` (requires Windows Build Support; the helper explicitly selects Mono and restores the previous scripting backend):

```sh
/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /absolute/path/to/Protobot-Rebuilt \
  -buildTarget Win64 \
  -executeMethod ProtobotEditorTools.WindowsBuild.BuildMono \
  -logFile /tmp/protobot-windows-mono-build.log
```

Distribute the entire Windows output folder, including `Protobot_Data`, `UnityPlayer.dll` and `MonoBleedingEdge`; the executable alone is insufficient.

After scene/prefab edits, check for C# errors, missing serialized references, runtime exceptions, and a clean `git diff --check`. Existing convex-mesh import warnings are known and are not compilation failures.

For initial hole placement checks, use `-executeMethod ProtobotEditorTools.HolePlacementValidation.Validate`. It checks scene rule ordering, serialized references, generated attachment geometry across part types and structural profiles, even hole counts, and rotated/opposite faces.
Use `-executeMethod ProtobotEditorTools.HolePlacementValidation.Smoke` without `-quit` for a Play Mode check of pointer raycasts, opposite-face input state, preview rule priority, the committed placement pose, repeated placement, and attachment refresh after property changes.

For modal transform regression checks, add `-executeMethod ProtobotEditorTools.KeyboardTransformValidation.Validate` to the headless compile command (or use Tools > Protobot > Validate Keyboard Transforms). This checks Main scene wiring and Preferences, disabled placement rotation, perspective/orthographic mouse projection, primary rotation axes, XYZ anchoring, snapping, smoothing, rigid MultiPivot behavior, duplication with undo/redo and cancellation, handle visibility, and input capture.

For grab hole snapping, run `-executeMethod ProtobotEditorTools.GrabHoleSnappingValidation.Smoke` without `-quit`. It exits Unity after checking XYZ/rebound input priority, H versus grid and rebinding, source picking, rigid face alignment, opposite faces, moving-collider exclusion, free-mode continuity, original XYZ/numeric anchors, selected-hole groups, and one-step undo/redo. It also runs `FastenerGroupingValidation.Check` for screw/collar physics detection, connection replacement, duplicate hits, occupancy, disable/re-enable, bounded shaft groups and Command-click individual movement. `FastenerGroupingValidation.ValidateRelated` runs the keyboard, numeric and property regression suites in one batch. `GrabHoleSnappingSetup.Configure` installs the binding, Preferences row and Main references idempotently.

For property and hole-tracking checks, use `-executeMethod ProtobotEditorTools.InteractionUpdatesValidation.Validate` (or Tools > Protobot > Validate Interaction Updates). This checks the new scene references/default P binding, dependent options, numeric limits, live hole orientation/position, geometry replacement, color, undo/redo, and generator parameter isolation. `InteractionUpdatesSetup.Configure` idempotently installs the new bindings/Preferences rows and keeps property input handling on Placement.

For a headless Play Mode smoke check, run Unity with `-batchmode -nographics -executeMethod ProtobotEditorTools.InteractionUpdatesValidation.Smoke` and omit `-quit`; the check exits Unity itself. It supplies virtual input devices and checks shaft geometry/detector resizing, undo/redo, persistent property input, the pick-part command, and preview updates.

For numeric-entry and shortcut-layout regression checks, use `-executeMethod ProtobotEditorTools.TransformEntryValidation.Validate` (or Tools > Protobot > Validate Numeric Transforms). It checks exact relative/global transforms, world Euler components, comma-separated coordinates, text input/capture, invalid-input recovery, slash cycling, pointer rebasing, rigid selection spacing, undo/redo, UI scaling and nonoverlapping visible shortcut rows.
