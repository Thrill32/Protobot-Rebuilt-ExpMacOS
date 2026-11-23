# Shared custom-model import investigation — November 18, 2026

This records the original 8 MiB failure and fix in `980c065`. The subsequent [larger-model update](LARGE_MODELS.md) raises updated helpers to 64 MiB while retaining honest feedback and old-helper limit negotiation. Original results and artifacts below are historical.

The reproduced failure occurs **before upload or an accepted document transaction**. A valid 16,300,084-byte binary STL parses locally but exceeds the existing **8 MiB encoded geometry** limit. The original player nevertheless displays `Submitted large-claw-size to the shared room.` Small OBJ/STL models work on both clients.

The reported workflow was a Mac artifact player joined to a CLI server on an 8 GB Pi 4, importing a 16.3 MB VEX claw STL. The original VEX file and Pi were unavailable for this investigation. A generated binary STL of the same size reproduces the reported message and missing model on an independent local CLI server. This establishes the failure mechanism; it does not establish execution on that Pi or prove every property of the original file.

## Checkout and actual artifacts

- Work began with a **clean** tree at `f2c2459d51949d8127b60ad6b1676d8dd0866035`, on `network-collab-support`, three commits ahead of its remote. No pre-existing uncommitted upgrade work was present. The supplied `c98e9feb...` exists but is not an ancestor of this HEAD; its production runtime source matches this HEAD. No history was changed.
- Read `AGENTS.md`, custom-model/collaboration sections of `CONTEXT_MAP.md` and `VALIDATION.md`, `ADDING_PARTS.md`, `ACCEPTANCE.md` and `UNITY_6000_6_9.json` before implementation. The upgrade's automated passes did not prove this import workflow.
- Retained player: `artifacts/unity-6000.6.9f1/players/Protobot.app`, Unity **6000.6.9f1 (8c45277fb9c5)**. Mac players use their embedded `Contents/Helpers/Collaboration/protobot-collab`; editor checks use `artifacts/osx-arm64/protobot-collab`. CLI reproductions explicitly select their independent executable. The helper paths and catalogs are captured in per-process reports.
- Original source, loose helper and embedded helper catalogs match: catalog ID `453a571c859f7f347abee11a12d4b81b489e47d0d98a46a562e19459c3d4b9ad`, file SHA-256 `1b3e43ec321283519b12207207d52d4d5762feb9730328dcb1bcf3e14486b8fd`. Original helper `Shared.dll` SHA-256 is `bb1ca12f2256fe3dd5422a8ab2612f98d3b4b214a502f9da548010cd297a0d2f`. Stale binaries/catalog mismatches were not the reproduced cause.
- All profiles, room stores, source fixtures, saves and rebuilt artifacts are disposable and task-owned. `Main.unity`, retained upgrade packages and existing local projects remain intact. No Pi update, deployment or protocol/storage migration was performed.

## Failure trace

| Stage | Original behavior with the oversized fixture |
| --- | --- |
| File → Import Model | Source units selected; Choose invokes the asynchronous model picker |
| Parsing | Success: 326,000 triangles / 978,000 vertices, below the local 64 MB / 500,000 triangle / 1,500,000 vertex limits |
| Placement | Import creates a temporary body at the local camera focus. This route has no separate placement preview or confirmation |
| Geometry publication | `GeometryTransfer.Encode` / `DocumentModel.ValidateAsset` exceeds the 8,388,608-byte bounded serializer before authenticated HEAD/PUT or `AddParts` is sent |
| Error projection | Mono's generated array writer wraps `CollaborationException: local-serialization-limit` in `TargetInvocationException`; the fixed error vocabulary projects that wrapper to `invalid-server-response` |
| Feedback | The fire-and-forget `PlaceCopies` returns after reporting the failure; `ReadAndCreate` immediately overwrites it with `Submitted … to the shared room.` |
| Accepted state | Revision stays **4**, with **4 small model instances / 2 geometry assets**. Both client document hashes and scene counts remain unchanged |
| Rendering/colliders/editing/persistence | The failed model never reaches these stages. Small models have working meshes, shaders, collider ray hits, selection, edits and persistence |

`original-array-serializer.log` traces the untouched upgrade player's managed assembly, MVID `7798179e-89eb-4952-a3f7-1d8f6f63c96a`, inside Unity. Its exact inner exception chain confirms the wrapper. The baseline diagnostic player changes only explicit development automation; the import/session/shared runtime under test remains the original implementation. `baseline-cli-confirmed/steps.json` records both editors reproducing the false message.

## Changes

- `LocalJson` records when its own bounded stream overflows and preserves `local-serialization-limit` through Mono wrappers. It does not unwrap arbitrary server errors or relax the fixed error vocabulary.
- Shared import preflights encoded geometry on the parsing worker before allocating a Unity mesh/collider. The dialog and error explain the encoded limit and that OBJ/STL file size differs.
- `PlaceCopiesAsync` returns a result, cleans staging objects on failures and preserves authority/editor checks. `Submit` waits for authority acceptance **and local accepted scene application**. Import announces `Imported … into the shared room` only after that succeeds; upload/transaction/apply failures retain their feedback. Failed uploads also clear pending selection callbacks.
- Reading/preflight remains cancelable. Once publication starts, the dialog waits for an outcome and Escape cannot imply cancellation of an already-sent edit. Existing membership, pending-receipt recovery, ownership, geometry quotas and local save data remain unchanged.
- Added a Mono serializer regression, .NET exact-boundary checks and repeatable two-player UI regression automation. Updated the context and validation maps.

The 16.3 MB fixture is intentionally still rejected. Reducing mesh detail to fit the encoded limit is required; the fix does not silently simplify geometry or increase quotas.

## Validation and evidence

Raw evidence is retained under ignored `artifacts/custom-model-investigation/`, including initial unsuccessful harness attempts. `verification.json` records the final artifact/check identities.

| Check | Result |
| --- | --- |
| Baseline CLI reproduction | Both independent editor players import small OBJ/STL; both oversized imports falsely say “Submitted”, with revision/assets/scenes unchanged |
| Final rendered in-app Host + joining editor | Both roles import OBJ and STL; 4 instances / 2 assets converge at revision 4; oversized imports preserve state and show the explicit 8 MiB error; all 4 bodies can be selected/edited, reaching revision 8 |
| Final rendered independent CLI server | Same import/edit checks using the **original catalog**; both clients save and reload their own `.pbb`; same-membership reconnect and durable CLI restart preserve geometry/poses after source files are deleted |
| Rendering and colliders | Both clients' screenshots inspected; shader support, mesh counts, per-body collider ray hits, stable IDs and world poses agree with accepted room state |
| Existing custom-model smoke | Passed parser, asynchronous import/cancellation, mounts, undo/redo, fasteners, independent duplicates and embedded save/load checks |
| Existing collaboration editor checks | Core, connection, recovery and UI passed; final core also includes the focused exact-boundary/mid-array Mono regression |
| Full collaboration lab | **1,616 assertions passed**, 10 clients on WebSocket, long polling and short polling; original 0.7/0.8/0.9 fixtures, geometry/auth/quotas/history/durability checks included |
| Existing final packaged-player pairs | **8 assertions passed**, four independent player processes, WebSocket ↔ long polling and WebSocket ↔ short polling |
| Builds/packages | Final Mac arm64 and Windows x64 development players built with 6000.6.9f1; all four helpers republished separately; embedded/source catalogs and managed helper assemblies checked; final Mac signature verified |

The UI automation invokes the actual File-menu button, unit/Choose controls, asynchronous callback dispatch, production parser/upload/transaction/materialization, G confirmation, save and open paths. **Only the OS picker is substituted.** Native macOS automation was unavailable (System Events denied Apple events; desktop capture unavailable). Player-generated screenshots supply rendered evidence. An initial graphics assertion under `-nographics` and a batch-mode screenshot attempt failed; corrected rendered windowed runs passed, with all attempts retained. These results do not establish native-panel focus, Windows/Linux execution, Pi execution or the original VEX claw file.

To rerun, use a **new** output directory:

```sh
python3 Collaboration/tools/check-custom-model-players.py \
  --player Collaboration/artifacts/custom-model-investigation/fixed/players/Protobot.app \
  --output /private/tmp/protobot-custom-new-run

# Independent CLI, compatible original catalog, and durable restart:
python3 Collaboration/tools/check-custom-model-players.py \
  --player Collaboration/artifacts/custom-model-investigation/fixed/players/Protobot.app \
  --cli Collaboration/artifacts/custom-model-investigation/fixed/helpers/osx-arm64/protobot-collab \
  --catalog Collaboration/artifacts/unity-6000.6.9f1/osx-arm64/catalog.json \
  --output /private/tmp/protobot-custom-new-cli-run
```

## Trying the corrected player with an existing room

The corrected app is `artifacts/custom-model-investigation/fixed/players/Protobot.app`; Windows output and complete helper distributions are alongside it. Building regenerates the conservative source catalog fingerprint. For an existing room using the upgrade catalog, run **`artifacts/custom-model-investigation/fixed/Launch existing room.command`** (Mac) or `Launch existing room.cmd` (Windows). These pass the preserved original catalog through the existing `--collab-catalog` option; Join remains explicit and credentials stay out of arguments. Original-catalog compatibility was exercised against both the retained CLI helper and the rebuilt helper. The original Pi room can remain untouched. Use the plain app for fresh rooms with the new default catalog.
