# Larger shared custom models — November 18, 2026

Updated players and helpers support **64 MiB of encoded geometry per custom model**, up from 8 MiB. A valid 16,300,084-byte binary STL in the focused test encodes to **43,578,311 bytes** (41.6 MiB), so a 16 or 32 MiB encoded cap would still reject it. The rendered Unity fixture also has a 16,300,084-byte source and encodes to **43,415,921 bytes**, with 978,000 vertices and 326,000 triangles. Both are synthetic valid precision fixtures; the original VEX claw file remains untested.

The previous [false-success fix](CUSTOM_MODEL_IMPORT.md) is retained. Success still requires authority acceptance and local materialization; failures never claim an import succeeded.

## Limits and compatibility

| Resource | Current bound |
| --- | --- |
| One encoded custom asset | **64 MiB**, formerly 8 MiB |
| Unused uploads before placement commits | **64 MiB or 16 assets**, formerly 16 MiB or 16 assets |
| Total retained encoded geometry per room, including history | **64 MiB**, unchanged |
| Unique decoded primitive arrays per room/client | **128 MiB**, unchanged |
| Decoded primitive-array cost summed over live model instances | **128 MiB**, unchanged |
| Multi-room process geometry | **128 MiB encoded / 256 MiB decoded / 256 MiB live-instance cost**, unchanged |
| Transfers | **One per room, two per multi-room process, two-minute request deadline**, unchanged |
| Ordinary metadata snapshots / wire messages | **2 MiB / 4 MiB**, unchanged; geometry uses authenticated HTTP |
| Source import | **64 MB file / 500,000 triangles / 1,500,000 vertices**, unchanged |

The unused allowance must fit one full asset because geometry is durably uploaded before an `AddParts` reference can commit. The total room limit still applies. Repeated instances reuse one encoded asset but each counts toward live-instance costs. These logical bounds do not promise total RAM or performance on a Pi.

Capabilities advertise `geometryLimitsVersion: 1` and `geometryAssetBytes: 67108864`. Updated Join requests acknowledge the limits version. The server rejects older clients **before creating or resuming membership**, so they cannot enter a room and then fail to hydrate a larger model. New clients reject unsupported advertised limits before admission and validate/encode against the selected server's actual bound. CPU-heavy encoding runs on a worker, keeping Unity's main thread available.

New players still support older 0.9 helpers. Missing capability fields mean the original **8 MiB** limit; their import dialog and errors report 8 MiB and explain that the server needs updating. They do not send oversized geometry to an old helper. Existing small-room editing/save/reconnect remains usable. An old player may display a generic request rejection when pointed at the updated server; update every player.

Wire version **0.9.0-alpha**, envelope/document/storage/archive formats, authentication, owner/editor/viewer checks and geometry hashes remain unchanged. Existing room identities, memberships, receipts, history and catalogs need no recreation. Once a larger asset is retained, an older 8 MiB helper cannot reopen that room, even if its visible instance was deleted: geometry retained by history still counts. Keep a stopped pre-update backup and the original helper/catalog if rollback is needed.

## Validation

Evidence is retained under ignored `Collaboration/artifacts/custom-model-limits/`. All profiles, source files, room stores and saves are disposable. Earlier upgrade and false-success-fix artifacts remain preserved.

- Focused .NET check: **14 assertions passed**, using the production OBJ/STL parser linked into the lab. It reads a real 16.3 MB binary STL, verifies encoded size beyond 32 MiB, authenticated upload/reference creation across WebSocket and long polling, a joining editor's leased edit, same-membership reconnect after source deletion and SQLite restart. It also checks missing/unsupported capability fields and explicit legacy-client refusal.
- Full collaboration lab: **1,630 assertions passed**, ten clients across WebSocket, long polling and short polling, including original 0.7/0.8/0.9 helper fixtures, authority/quotas, failed storage writes, history, backup/restore, recovery and larger-model checks.
- Rendered two-player CLI and in-app Host/Join runs: small OBJ/STL imports plus one 16.3 MB STL import from each role produce **six instances / three immutable geometry assets**. Both large instances are selected, edited and saved; all clients converge at **revision 12** after the six edits. Geometry and poses survive reconnect, source-independent `.pbb` reload and, for the CLI run, a durable server restart. Both clients reload their own CLI saves.
- A valid 500,000-triangle source whose encoding exceeds 64 MiB is rejected before mesh creation/upload, preserving accepted hash, revision, assets and scene count. The actual negotiated limit is visible in the error.
- A rendered run against the retained original helper keeps its real 8 MiB limit, imports the small models, honestly refuses the precision STL, and preserves save/reload/reconnect. Actual old-player/new-helper admission is checked separately against read-only membership diagnostics.
- Focused Unity Mono serialization and existing collaboration core, connection, recovery and UI checks run with fresh profiles. Mac arm64 and Windows x64 players plus four native helper distributions are rebuilt separately. Final catalogs, helper inventories and Mac signatures are checked.

The import harness invokes the real File-menu, units/Choose, asynchronous callback, parser, upload, accepted transaction, materialization, G confirmation and save/open paths in independent players. Only the OS picker is substituted. Native focus, the original VEX file, Pi execution and Windows/Linux runtime execution remain field checks. See `verification.json` for final per-check and artifact identities; failed harness attempts are retained separately.

## Trying the update

New artifacts are under **`Collaboration/artifacts/custom-model-limits/`**:

- Mac: `players/Protobot.app`.
- Windows: the complete `players/Windows/` directory.
- Pi with a 64-bit ARM Linux OS: the complete `helpers/linux-arm64/` directory. Keep its runtime files together; the executable alone is insufficient.
- Other helpers: `helpers/osx-arm64`, `helpers/win-x64`, `helpers/linux-x64`.

For an existing room with the original upgrade catalog, use **`Launch existing room.command`** (Mac) or `Launch existing room.cmd` (Windows). They pass a preserved original catalog through the established `--collab-catalog` option. Join and credentials remain explicit. The new helper loads an existing room's stored catalog; do not recreate or reinitialize its data directory to update it.

Update **the server helper and all players**, following the existing [installation/update workflow](INSTALLATION.md), with the service stopped and an independently retained backup. Starting the updated player alone against the old Pi helper retains the 8 MiB cap. No deployment, Pi access, room overwrite, catalog rewrite or migration of existing user data was performed here.

To rerun the focused regression from `Collaboration`, use the pinned SDK and a rebuilt lab:

```sh
DOTNET_HOST_PATH=/absolute/path/to/dotnet /absolute/path/to/dotnet \
  tools/Lab/bin/Debug/net10.0/protobot-collab-lab.dll --large-model-only
```

To rerun the rendered CLI workflow from the repository root, choose a new output directory:

```sh
python3 Collaboration/tools/check-custom-model-players.py \
  --player Collaboration/artifacts/custom-model-limits/players/Protobot.app \
  --cli Collaboration/artifacts/custom-model-limits/helpers/osx-arm64/protobot-collab \
  --catalog Collaboration/artifacts/unity-6000.6.9f1/osx-arm64/catalog.json \
  --output /private/tmp/protobot-large-models-new-run
```
