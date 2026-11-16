# Collaboration development alpha

This implementation prioritizes Host/Join and everyday editing. It is opt-in and is **not the completed collaboration release** described in the local `/Notes` plans. In-app hosts use durable local rooms with restart recovery, persistent membership/history, shared sprocket chains, bounded content-addressed custom geometry and confirmed live custom mounting-point layouts. Dedicated hosts can serve up to eight durable rooms through one listener, with a private server administration capability, atomic creation/restore registration, isolated recovery and process budgets. In-app hosting keeps its existing single-room workflow. Complete release and deployment acceptance remains pending.

For a **dedicated Debian room**, use the [Debian/Caddy deployment guide](deploy/README.md). Self-contained CLI artifacts support both x86-64 Debian hosts and 64-bit ARM (`aarch64`) hosts such as Raspberry Pi 4. The CLI initializes a durable room from a matching catalog or portable accepted-build export, and `doctor` checks authentication, WebSocket/request transports and sustained event delivery. The guide includes a systemd unit and HTTPS proxy configuration. Earlier user-reported public-server testing does not establish 0.9 multi-room field acceptance; formal cross-platform/release checks remain pending.

Hosting is not tied to 443: `serve --listen` selects the backend port, while clients and `--public-url` accept an explicit HTTPS port such as `https://cad.example.org:80` or `:8443`. The [custom-port Caddy template](deploy/Caddyfile.custom-port) supports any available public port with an existing valid certificate; its configuration avoids reserving 443 or a plaintext HTTP listener. See the guide for certificate renewal and service settings.

The same self-contained `protobot-collab` executable serves an editor-owned room and a standalone single-room or multi-room host. Unity and the bots compile the **same** protocol, reducer and client sources in `Packages/com.protobot.collaboration/Runtime`. There is no Unity server, BinaryFormatter network payload, hosted account or mandatory relay.

## Try Host/Join

1. Install the SDK pinned by `global.json`. From `Collaboration`, run `bash publish.sh osx-arm64` on a Mac, or `./publish.ps1 win-x64` in PowerShell on Windows. Publish each matching helper before making its player build. Generated helpers and players stay in ignored `artifacts/`. The publish scripts select the matching platform dependency lock and enforce it; use these scripts for local helper publishing.
2. In Unity, use **Tools → Protobot → Collaboration → Export Catalog**, then **Enable Development UI**, and enter Play Mode. This installs the controller at runtime; `Assets/Scenes/Main.unity` is unchanged. The separate **development player** exposes the **Collaboration (alpha)** button on ordinary launch, with no terminal flags needed. Release/offline builds remain opt-in through `--collaboration`.
3. Open **Collaboration (alpha) → Host**, enter a name, room ID and optional password, and select **Host current build**. Port zero chooses an available port. Loopback allows another process on the same computer. For a second LAN computer, explicitly enable LAN HTTP and use a reachable LAN address/port; firewall/reachability is untested until that computer connects.
4. Select **Copy invite**. In the second instance, use the same catalog/build, open **Join**, select **Paste invite from clipboard**, enter the password separately, and choose **Join room**. Enable LAN HTTP on that computer for an HTTP LAN address. Existing unsaved changes use the ordinary Save/Discard flow. A newly joined document starts with Save As; hosting preserves the host's path. **Join as viewer** creates a read-only membership; a resumed identity keeps its existing role/history.

For one-Mac testing, double-click `Collaboration/artifacts/players/Protobot.app`, host a room, then select **Open another local test window** in its collaboration window. That button launches the same player with a separate persistent profile and a prefilled Join page. It does not open another Unity editor, autojoin, or send a password. Keep the first window running, enter the password in the second if needed, and select Join. The second window's own log and recovery identity stay under its test profile. This launch shortcut is available only in built macOS/Windows players; command-line profiles remain useful for repeatable named test instances.

The session page shows connection state/transport/revision, participant roles, local file save status, independent Save/Save As actions, reconnect/restart and deliberate Leave/Stop hosting. Leaving through the UI waits for pending outcomes to be resolved and explains the effect on membership and other participants. Escape or Close dismisses the window and restores editing; the compact button remains visible. The scrollable modal canvas owns pointer input and text entry while the scene tools are paused. Connection details include the protocol/catalog fingerprint and selected transport, without exposing recovery credentials.

Host, Join, Resume and Reconnect show their current connection stage. Geometry synchronization shows the model number, transferred MiB and known encoded size; retries report the attempt number. **Cancel**, **Close** and **Escape** cancel the active connection. The Cancel button keeps the session window available for retry; Close/Escape return to the editor after cleanup. Connection work keeps scene editing paused until it completes or cancels.

Canceling a join preserves the current build and its local file. Canceling reconnect preserves the last accepted scene and unresolved edit receipt IDs; retry loads a complete current snapshot and resolves outcomes without replaying commands. A returned membership credential and sequence are saved before cancellation is honored, so a known membership resumes with its history. An already-sent admission request can take up to ten seconds to settle its reply; the UI shows that cancellation is finishing. A lost reply/network failure remains a connection failure with the usual explicit retry/recovery path. Cancellation retains membership/cache data and does not perform Leave. Canceled owned Host/Resume startup stops its own helper and retains any complete durable room for explicit recovery. Startup bootstrap serialization runs off the Unity frame thread within a thirty-second startup bound. Existing independent servers continue running.

Progress contains fixed stages, counts and byte lengths; it does not include source filenames, geometry hashes, endpoints, passwords or capabilities. Unity retains one latest progress value per attempt, and late progress from an ended attempt cannot replace a newer operation's status. Confirmed mounting-point edits are included in the displayed editing coverage. This slice changes no wire/storage/archive/registry format; the source catalog fingerprint changes, so retain the original matching catalog for existing rooms.

**Export current build for CLI...** writes a portable JSON catalog/document snapshot without session secrets or personal history. It captures accepted content before the file dialog, leaves later remote edits out of that export, and does not change your local `.pbb` path or saved marker. `init --snapshot` imports it as a new room/generation. On Join, **Test server connection (read-only)** checks the selected transport inside Unity without opening the remote document; it uses temporary viewer memberships and never makes an edit. Passwords stay outside diagnostic reports.

Use the regular add menu with hole/hardware/gear snapping, G/R, transform handles, Move (including G → H), Connect, paint/Default, existing-part properties and Delete. Custom OBJ/STL import parses locally and publishes validated immutable geometry without exposing its source path. M mode supports custom mount add/delete, G/R/copy, B depth and N grid edits while connected. Each gesture reserves the complete owning custom-model set before changing the local layout; temporary rings, cursor motion and canceled edits remain private, with no ghost or document mutation. Confirmation publishes one ordered whole-model mounting layout, while the authority rejects stale versions and any attempt to change the immutable geometry, pose, identity, state or paint through that mutation. Alt copies commit movement and new parts together, and also duplicate a contained chain when all of its endpoints are copied. P stamps frozen selections; clipboard paste and build import accept catalog and custom content, remapping any contained chains atomically with their copied endpoints and reusing matching geometry hashes. Malformed geometry/chains, physically incompatible sprocket sets and transfers beyond the 256-mutation bound are refused before shared placement. Select compatible coplanar sprockets and press C to add or remove their shared chain. Targeted operations carry the versions read while resolving geometry and reject if those targets change. Leases cover the complete frozen write set of existing entities, including every existing chain endpoint; fresh endpoints created by one copy/import do not need a pre-creation lease. A waiting reservation is shown in the collaboration status. Other participants see cyan mesh ghosts during G/R; cancellation removes them. Incoming poses are resolved results, not mouse gestures replayed on another client. Accepted poses retain selection, and deletion clears invalid selection while atomically removing incident chains. See [mutation coverage](MUTATION_COVERAGE.md).

Undo/Redo applies to your accepted edits in the current room membership. The authority supplies inverse content, checks live reservations and refuses an inverse after another participant touches any affected part or chain. Deletion undo restores the same stable part/chain IDs with newer versions; copy-and-move and part-plus-chain deletion remain atomic. Reconnect and server restart preserve this history when you resume the same membership. Explicit Leave revokes that membership; a later new membership starts a new history. Offline history is separate.

When updating this alpha, use a matching player/catalog for each selected room and **republish the helper on each host**; an old helper cannot accept the new commands. Run the publish command in step 1 and Export Catalog again before testing. Recovery requires the original matching catalog and matching 0.9 clients/helpers. Original 0.7/v1 rooms migrate atomically on first open; see the upgrade procedure below. Keep an original backup before upgrading. Other incompatible rooms fail closed. The user reported basic G/R, add and delete working across two Windows editor runtimes plus one macOS editor runtime on November 8, 2026; expanded tools still need that manual cross-platform pass.

Each participant saves the current **accepted** document using their own camera, path and recent history. Save waits for that participant's submitted edits, excludes previews, and leaves later remote changes dirty. Disconnection freezes shared edits. Reconnect resolves pending receipts and loads a fresh consistent snapshot; it never automatically resubmits an unknown operation. Leave/Continue offline forks the last accepted document. Open/New leaves the room after local unsaved handling. A host's quit/crash closes its private pipe and ends only its owned helper; independent servers are unaffected.

## Recover a room

The host commits each accepted edit, receipt and personal history change in one SQLite transaction before acknowledging it. The store uses WAL with `synchronous=FULL`, an exclusive room-directory lock and versioned hashed document checkpoints plus journal replay. Use local storage; SQLite WAL is not supported on a network filesystem. SQLite recovery and participant `.pbb` Save are independent. A storage write failure freezes the room until restart; an unreadable or incompatible database is retained and never replaced by an empty document.

If the owned helper dies while the editor stays open, shared editing freezes. Select **Restart host and reconnect** to reopen the same directory on the same port. Participants reconnect with their cached identities and resolve pending receipts. Leases, bearer access tokens and ghosts are discarded on restart; resume credentials, receipts and personal history survive. Unknown pending operations are never automatically resubmitted.

After an application restart, enable/open the collaboration panel. **Recovered rooms** lists local stores without opening a listener. Select **Resume** on the desired room, using the normal unsaved-change flow. This opens accepted content as a new local Save As document. LAN exposure still requires the explicit LAN toggle; the saved port is reused unless edited. Other computers join/reconnect to that endpoint using their same local profile and catalog. A participant cache is scoped to origin, room, catalog and generation; changing the endpoint may start a different membership. **Forget saved membership** explicitly discards a guest cache when starting fresh is intended.

Explicit Leave/Stop hosting revokes that membership before stopping the owned helper. Closing the application preserves its resume cache instead. Recovery files contain private capabilities; keep them protected. Dedicated CLI rooms support [offline backup/restore](#offline-backup-and-restore); in-app recovery-directory backup and general storage/catalog migration remain unfinished; the bounded 0.7/v1 → v2 upgrade below is supported. Keep the matching build/catalog available when testing restart recovery, and use `.pbb` Save to carry accepted content into a new build.

## Owner participant management

Owners can use **Refresh participant management** on the session page to list stable membership IDs, roles and online/offline status. Select **Remove ...**, check the displayed name and ID, then confirm **Remove membership**. Cancel leaves the membership intact. Editors/viewers have no management controls and the authority rejects their requests. Owner memberships, including your own, are protected from this operation; deliberate Leave remains separate.

Removal persists the existing membership revocation before returning success, invalidates active and saved resume access, releases the target's leases and ghosts, and wakes/terminates WebSocket and held-request event connections. The removed player pauses shared editing, explains the removal and offers Continue offline with its last received accepted build. Pending outcomes that were not received before removal cannot be queried through that membership; consult the owner before recreating them. Its private recovery cache remains terminal until explicitly forgotten. Accepted document content, receipts and all personal histories stay in the room; administration does not change document revision or enter Undo/Redo.

Commits, resume and removal serialize through the same room gate. A commit accepted before removal remains accepted; later commands fail. If resume wins first, its new access token is revoked too. Removing an already revoked ID succeeds with `alreadyRevoked: true`, including after restart; a missing ID fails, and a protected owner always fails. Storage failure never reports success: the room freezes until restart, which reads the committed state. If a reply is lost, retry the same stable ID or refresh its revoked status.

**This revokes one membership, not a person.** Anyone who still knows the room password (or anyone in a blank-password room) can create a fresh membership with a new ID/history. No person ban, password rotation or separate admission policy is implemented.

For an initialized dedicated CLI room, use its protected `owner.json` while the server is running:

```sh
protobot-collab participants --url https://cad.example.org --owner-file /private/path/team/owner.json
protobot-collab remove-participant --url https://cad.example.org --owner-file /private/path/team/owner.json --participant <stable-id>
```

`participants` returns all retained memberships, including revoked and offline records, with `participant: { id, name, role }`, `active` and `revoked`. Activity means a request within the last 60 seconds, rather than proof of a live socket. Copy the exact ID from that list; the removal command is an explicit administrative action without an interactive confirmation. `--owner-file` supplies only a file path in arguments; the capability travels in the authenticated request header. The commands create no temporary owner membership, require no room password/catalog and refuse redirects. Use `--allow-lan-http` only for an explicitly trusted LAN; loopback HTTP works without it. Unix owner files must have no group/other permissions (normally 0600); Windows ACLs must grant access only to the current operator. Symlink files and broad permissions are refused. Run as the room's service account or use a privately protected operator copy; never paste the capability into commands, invites or logs. Management failure prints a bounded HTTP status and recognized error code; untrusted response text is suppressed.

Participant administration originated as an additive `participantAdministration` capability in 0.7 and continues in 0.8. The new storage migration preserves the same revoked flag, owner capability and membership IDs. Original 0.7/v1 backups restore forward; v2 rooms/archives require 0.8 and cannot be opened by an old helper. Catalog matching remains mandatory, including the explicit original-catalog option below.

## Catalogs, chains and private animation

Export Catalog generates `Assets/Resources/Collaboration/catalog.json`. It describes built-in part IDs and valid parameter combinations (for example `SHFT-Normal-6`), plus a compatibility fingerprint of the generation code and assets. The standalone server uses these rules to validate edits without Unity; participants generate the matching geometry locally. This is separate from your `.pbb` document and does not export your current parts or enable additional tools. The current fingerprint conservatively includes all runtime scripts, so re-export after code/catalog changes, not after ordinary edits or saves. Player builds and the local/CI helper publication scripts bundle the tracked catalog automatically.

Custom meshes are immutable assets keyed by the established canonical SHA-256 hash (name, float coordinates and indices). Each `CUSTOM` part references one hash and keeps its own ordered mounting layout. Geometry uploads persist before the first referencing edit is acknowledged; copies and later edits reuse the hash. Names, coordinates, indices, counts and hashes are validated. Remote mount apply rebuilds only mount children, leaving geometry/mesh/material ownership intact. Undo/redo, accepted `.pbb` saves, portable room exports and recovery retain confirmed layouts. Preview/canceled layouts remain private.

`HEAD/GET/PUT /rooms/{room}/assets/{lowercase-sha256}` uses bearer membership authentication outside events/previews. Owners/editors upload; viewers download. No endpoint accepts a filesystem path. Every 64 KiB transfer buffer and final upload publication rechecks authorization, including revoked membership. HEAD avoids resending existing geometry. One transfer runs per room; excess requests receive a bounded busy response and the client retries. An interrupted request discards its partial body. Immutable upload/download retries start from byte zero (two network retries; up to 30 busy retries, all within a two-minute deadline); range resume is not implemented. Downloads verify geometry and hash before caching/materializing. The join snapshot stays at revision R while assets download, then the ordered event pump replays edits after R; clients enable editing only with a fully hydrated accepted snapshot. Event messages and Unity's metadata queue never carry geometry arrays.

Separate limits are **8 MiB encoded JSON per asset, 64 MiB stored geometry per room, 128 MiB decoded unique primitive arrays per client/room, a separate 128 MiB primitive-array budget summed over live custom instances, 256 total assets, and 16 MiB or 16 unused uploads**. These bounds include assets retained by history, even after instance deletion. A failed/canceled create may leave an unused upload; reuse its hash or start a new room if that allowance fills. No garbage collection, upload deletion or history compaction is included. The ordinary **2 MiB reference snapshot / 4 MiB wire-message limits stay unchanged**. Hydrated bootstrap and portable local export serialization has a separate 72 MiB cap; `.pbb` saves embed each instance's geometry and remain independent of server storage.

Quota measurement on macOS arm64 with pinned SDK 10.0.403: a deterministic 90,000-vertex / 30,000-triangle mesh encoded to **5,426,656 bytes**, with 2,520,000 bytes of primitive arrays. The complete expanded local lab (including repeated near-64-MiB quota filling, both transports, recovery and migration) used approximately **652 MiB maximum RSS / 196 MiB peak footprint** in `/usr/bin/time -l`; that is a test-process measurement, not a per-client or target-platform performance promise. The 8 MiB asset cap leaves margin over this tested mesh while bounding whole-object JSON parsing/retry; the 64 MiB room and 128 MiB array caps limit retained/cache expansion; the separate live-instance sum also bounds mesh and accepted local-save expansion when many copies reuse one asset. Local source import still permits 64 MB files, 500,000 triangles and 1,500,000 vertices, but collaboration can refuse an otherwise valid offline mesh if its encoded size or room budget exceeds these limits.

Chains are logical versioned document entities with stable IDs. Legacy routes retain unordered outer-hull membership; version 1 stores cyclic sprocket order and explicit per-member inner/outer wraps. Shift plus the Chain action opens the numbered route editor; Apply publishes `SetChains` with the existing identity. Cyclic rotations/reversal canonicalize with wraps attached to their members, and hashes/history include route choices. See [chain controls and format](../CHAINS.md). The authority validates endpoint existence, rejects duplicate topology, and requires one lease covering every endpoint for add/remove. Clients recreate the contour from the accepted topology; the server does not evaluate Unity geometry. Hosting, Join/reconnect, `.pbb` Save, portable room export, deletion, restart recovery and personal undo/redo all retain accepted chains. Chain contours and simulation movement are derived locally, while C publishes only the confirmed topology change.

Simulation mouse scrubbing, numeric entry and timeline playback are private. They send no ghost frames and hold no edit reservations. A mouse confirmation (including numeric Enter) or the timeline **Set pose** button freezes the resolved world poses, reserves every changed part and submits one undoable transaction. Other participants see only the accepted result. Save still writes the accepted baseline during a preview. The animation controls, clock, speed and selection remain local.

Mechanism discovery currently reads the complete scene: any accepted shared edit resets an active private animation before applying the edit. A commit also carries its starting document revision, so a new or moved part cannot silently change the mechanism between evaluation and acceptance. A denied reservation, stale graph or disconnect restores the preview. Undo/redo acts on the whole accepted pose. Normal Move continues using its existing alignment/rotation math and per-entity target versions; the legacy shaft-position buttons now submit resolved poses too.

This pass requires **0.9.0-alpha** helpers/clients, document schema **2**, SQLite storage **2**, and backup archive **2**. All participants must upgrade together. Wire transactions retain envelope protocol 1 but asset writes now contain references; schema-2 document hashes include immutable asset references rather than inline arrays. Old clients are rejected explicitly.

To upgrade an existing **0.7/v1** room without losing its membership/history:

1. Stop the original helper/service and retain its complete original helper/player/catalog plus a private v1 backup. Keep the original stopped during migration and restore testing.
2. Start the 0.9 helper against the original directory, or run 0.9 `restore` on the v1 archive into a new directory. The loader fully validates/replays v1 before committing the asset table, reference checkpoint and storage-version change in one SQLite transaction. Over-quota legacy content is refused without dropping it; reduce it with the original build and retry. A failure leaves the original room unmigrated; restore failures never publish the destination.
3. Use upgraded clients with the **original matching catalog** and existing local profile/cache. Launch Unity/player with `--collab-catalog /absolute/original/catalog.json` when its bundled fingerprint differs. This explicit local option is for unchanged part-generation builds, not arbitrary catalog substitution; do not replace room IDs/catalog hashes or discard participant caches. Additional local test windows forward the option. There is no automatic catalog migration.
4. Resume/reconnect through the original endpoint. Generation, accepted revision, identities/password/owner credentials, revocations, receipts, personal history/cursors, ordered mounts and chain routes survive. A migration checkpoint requires old event cursors to resnapshot; retained journal/receipt/history costs remain bounded. Take a new v2 backup after verifying accepted content.

A v2 room/archive is **forward-only**: restoring the retained original v1 backup with the original executable is the rollback path, and excludes edits accepted after upgrading. Older 0.3–0.6 formats retain their existing validation/replay support before atomic migration; the original-0.7 test is the current compatibility acceptance target. The lab's optional `--previous-helper /absolute/original-0.8/protobot-collab` checks actual 0.8/v2 reopen/restore/registry registration, including owner identity, geometry, receipts and redo. Its `--legacy-helper /absolute/original-0.7/protobot-collab` builds a real v1 fixture/archive, verifies original hashes, injects migration rollback and checks preserved identities/revocation/receipt/history/mount/route behavior.

## Diagnostics and resource usage

In the collaboration window choose **Export diagnostics…** and a new JSON filename. Cancel and Close cancel pending export. A native file panel owns Escape and returns cancellation; afterward Escape closes the collaboration window. Export leaves the accepted scene, local save/file association, pending edits and membership alone, including after failure. It captures small in-memory metadata; it does not read private recovery files, databases, source models or player logs. Exports can also be made after a failed connection. Room owners can choose **Refresh room resource usage**; the view is an explicit sampled snapshot and is included in subsequent exports. Other participants export their own connection metadata. Older 0.9 helpers without the optional capability still support local exports; their detailed usage remains unavailable.

```sh
# Read-only inspection; these commands create no participant membership.
protobot-collab diagnostics --url https://cad.example.org --owner-file ./team/owner.json --output ./new-room-diagnostics.json
protobot-collab diagnostics --url https://cad.example.org --admin-file ./server/admin.json --output ./new-server-diagnostics.json
protobot-collab room list --url https://cad.example.org --admin-file ./server/admin.json
# Existing diagnostic probes remain supported (create/revoke bounded test viewers):
protobot-collab doctor --url https://cad.example.org --room team --catalog ./catalog.json --quick --output ./connection-report.json
```

`diagnostics` writes **format `protobot-diagnostics`, schema 1**: at most 64 KiB, eight room records and 32 recent events with fixed stage/recognized-code strings. Metadata includes protocol/document versions, room/catalog, accepted generation/revision/hash where known, selected transport, connection stage, elapsed/relative stage timings, latest model transfer counters and pending-operation count. Unknown values are null. Client events are relative to the latest connection attempt; exports include capture time in Unix milliseconds. Doctor retains its existing fields/exit codes and adds `format: protobot-connection-report`, `schema: 1` and bounded per-transport diagnostic metadata. Quick/capability checks still do not prove sustained transport acceptance. Export output uses exclusive atomic publication; an existing destination is retained, so choose a new name. Ctrl+C cancels the CLI work. No report includes endpoints, display names, credentials, private paths, document/geometry payloads or arbitrary log/proxy/error text. Reflected errors become allowlisted codes. Review room identifiers before sharing reports outside your team.

Owners can inspect **only their room**, through their owner capability or an owner access session. Participants/viewers/resume tokens cannot fetch room administration metrics. Server admin can inspect registered rooms and process usage but gains no room editing/membership rights. `/capabilities` only advertises optional `diagnosticsVersion: 1`; public health never exposes administration metrics. Version remains **0.9.0-alpha**; document/SQLite/archive stay **v2**, and registry/configuration/admin stay **v1**. The added capability is optional, with no data migration.

Usage shows participants versus the 10-active bound, retained memberships versus 1,000, active requests/event streams/transfers, retained geometry/assets, unused uploads, parts/chains, lifetime history cost, receipts/journal events and retained history-entry count. Every bounded resource pairs `used` with `limit`; unavailable counters are null. `retainedGeometryBytes` is encoded cached content, `decodedArrayBytes` is unique primitive-array cost and `liveInstanceArrayBytes` sums logical array cost across live copies. **These are not measured runtime memory.** Only server-admin reports additionally contain `measuredManagedBytes` (non-collecting GC estimate) and `measuredWorkingSetBytes` (current process working set), with no promised memory ceiling. Connections count actual listener connections; event streams count WebSocket/held-request streams. Request counters include the inspection request itself. Room/process counters are sampled independently and can change during collection; they are not an atomic online backup or continuous monitor. No database/directory sizes or filesystem scans are collected.

A locked, corrupt, incompatible, missing or process-over-budget registration retains its status and recovery guidance with **null usage/revision**, while healthy rooms continue. Inspecting an unavailable room never initializes, migrates, replaces or repairs its data. For a lock, stop the other process/check service-account access; never remove an active lock. For incompatible data use the matching release/catalog. For `server-geometry-quota`, use a separate host or reduce content with the matching release and restart. Preserve damaged originals privately before stopped-service backup restoration; never initialize over a registration. Frozen live rooms remain inspectable through owner/admin credentials using their last accepted in-memory counters. Garbage collection, history/receipt compaction, online backup and live destructive deletion remain separate work.

## Build players

The build methods below create separate development artifacts and do not overwrite `build.app`:

```sh
Unity -batchmode -nographics -quit -projectPath /absolute/project \
  -executeMethod ProtobotEditorTools.CollaborationSetup.BuildMacPlayer
Unity -batchmode -nographics -quit -projectPath /absolute/project \
  -executeMethod ProtobotEditorTools.CollaborationSetup.BuildWindowsPlayer
```

Use the actual Unity executable for `Unity`. Output: `Collaboration/artifacts/players/Protobot.app` and `Collaboration/artifacts/players/Windows/Protobot.exe`. Both include the matching helper, catalog and notices. The Mac helper is installed under `Contents/Helpers/Collaboration` before the final app signature. Windows ships a `Collaboration` directory next to the executable. Distribute the complete folder/app. An ordinary offline build does not require a helper; setting `PROTOBOT_BUNDLE_COLLABORATION=1` makes matching-helper packaging mandatory for other build entry points.

Launch the executable directly for independent local players, with unique profiles/logs:

```text
Protobot --collaboration --collab-profile "/tmp/protobot lab/alice" --collab-name Alice -logFile "/tmp/protobot lab/alice.log"
Protobot --collaboration --collab-profile "/tmp/protobot lab/bob" --collab-name Bob --collab-join http://127.0.0.1:8787 --collab-room team --collab-transport long-polling -logFile "/tmp/protobot lab/bob.log"
```

On macOS use the executable inside `Contents/MacOS`. A profile owns a file lock and its own atomic JSON preference store covering settings, rebinding, recent builds and default directories. Profile initialization errors fail rather than falling back to ordinary user preferences. Durable host metadata and scoped participant resume caches live under that profile's `Collaboration` directory (or ordinary application data without a test profile). These contain credentials: directories/files are private on Unix and inherit the user-profile ACL on Windows. Do not share them as test reports. The clipboard remains the real system clipboard. Recognized option values are excluded from startup `.pbb` file scanning. Never pass passwords or tokens as arguments.

## Fast validation

No Unity license is needed for the protocol/server lab:

```sh
cd Collaboration
dotnet build src/Cli/Cli.csproj
dotnet run --project tools/Lab/Lab.csproj -- run --bots 10
dotnet run --project tools/Lab/Lab.csproj -- run --bots 2 --transport long-polling
dotnet run --project tools/Lab/Lab.csproj -- run --bots 10 --player artifacts/players/Protobot.app
# Preserve the entire original helper directory before replacing it:
dotnet run --project tools/Lab/Lab.csproj -- run --bots 10 --legacy-helper /absolute/original-0.7/protobot-collab
```

`AdministrationChecks.cs` adds owner/editor/viewer authority, protected owners, inactive membership IDs, commit/reconnect/removal contention, duplicate and lost-reply retries, storage-failure freeze/rollback, restart, both event transports, CLI owner-file permissions and error redaction. `BackupChecks.cs` retains owner-removal revocation and idempotent administration after restoration. Unity's UI smoke drives owner refresh/removal/cancel/confirmation, terminal WebSocket feedback and offline retention, and reads earlier 0.7 capabilities; recovery rejects a removed cached identity after a real helper restart.

The lab checks model atomicity, numeric/catalog/custom-asset validation, geometry hashing/deduplication, mount-only replacement and immutable-geometry enforcement, independent expected poses, canonical hashes, disjoint edits, contention, fake-clock lease fencing, viewer/owner/password handling, duplicate operations, ghost cancellation, delete/tombstones, paint/replacement transactions, atomic transform-copy history, versioned chain add/remove, duplicate-topology rejection, atomic part/chain deletion, sequential personal undo/redo and stale inverse refusal, explicit reconnect, blocked-upgrade Auto fallback, lost acknowledgments, broadcast-before-ack, snapshot handoff during active edits and actual CLI EOF shutdown. Recovery checks cover SQLite checkpoint/tail replay, receipts, chains, custom geometry/mount layouts and personal undo across restarts, 0.3/0.4 format upgrades, old-token/lease fencing, failed-write rollback/freeze, exclusive storage/cache ownership, corrupt checkpoint rejection and a real CLI kill after a lost commit reply. A fresh client resolves its saved pending ID without replaying the operation. The lab checks the bundled native SQLite version against the WAL-reset fix. Deployment checks exercise real CLI init/doctor commands, portable content, overwrite refusal, password rejection, read-only/idle transport checks, upgrade-blocked fallback, untrusted TLS rejection, error-body redaction, standalone stdin independence and clean Unix SIGTERM shutdown. Optional `--player` launches two built development players with independent profiles, the real in-app helper, WebSocket and forced request transports; compares their canonical documents and independently checks scene poses/colliders/save snapshots. It terminates only processes it starts. Every run prints its isolated diagnostic directory with credential-free reports; generated run artifacts are retained.

Unity integration smoke (omit `-quit`; the test exits itself):

```sh
Unity -batchmode -nographics -projectPath /absolute/project \
  -executeMethod ProtobotEditorTools.CollaborationValidation.Smoke \
  --collab-profile "/tmp/protobot-collaboration-unity-profile" \
  -logFile /tmp/protobot-collaboration-smoke.log
```

This smoke includes custom geometry plus silent mounting leases, save-safe unconfirmed layouts, confirmed propagation, personal undo/redo, remote ordered-layout materialization and immutable-geometry checks.

A separate private-animation integration check uses `ProtobotEditorTools.CollaborationSimulationValidation.Smoke` with the same arguments. It tests a generated multi-part drivetrain, private mouse/timeline/playback, absence of leases/ghosts, baseline file saves, mouse/Set pose confirmation, undo/redo, remote invalidation, stale graph rejection and disconnect recovery.

A separate recovery check uses `ProtobotEditorTools.CollaborationRecoveryValidation.Smoke`: it kills the owned helper during a private preview, restarts/reconnects both identities, exercises persistent undo/redo, verifies local files stay independent and reopens the room through the explicit recovery path. `ProtobotEditorTools.CollaborationUIValidation.Smoke` exercises Main's real UI input module, modal pointer/text input, invite validation/paste, the Host button, participants/roles/save status, reconnect and deliberate leave. Use a fresh isolated `--collab-profile` for each smoke run.

The development-only player probe `--collab-probe ui --collab-test-results /absolute/path/ui.json` (with an isolated `--collab-profile`) captures Host/Join/session PNGs, launches a second player through the local-window launcher, joins through its prefilled UI as a viewer and checks the two-participant session. It closes its child after collecting results; close the probe parent afterward. This also checks ordinary development-player UI installation without `--collaboration`. Normal UI use needs no probe flags.

The core smoke exercises the managed host launcher, Unity's actual networking runtime, catalog/custom materialization, direct OBJ import, custom mounts/paint/copy/deduplication/history, paint/state preservation, G/R commit/cancel boundaries, accepted saves, remote deletion, path isolation and Host/Join/leave. It also drives paint/Default, property regeneration/selection, personal history, G → H, Move, pointer handles, Connect, Alt copies, P and clipboard paste. The simulation smoke drives the real C-key chain path, materialization, atomic chain clipboard paste/build import, accepted-file persistence and chain history in addition to private animation behavior. Batch Mode invokes gesture boundaries explicitly because it has no focused game window. Existing keyboard-transform, Escape, selection, build-file and transfer suites remain the input/offline regressions. Windows/Linux script compilation uses `ProtobotEditorTools.FilePickerPlatformValidation.CompilePlayers`. Cross-OS manual operation, trusted HTTPS/proxy inspection and complete Debian service acceptance must still be verified before release; the user has separately reported successful live public-server operation.

## Standalone server

Initialize an empty durable room with the exact player catalog, or seed it from **Export current build for CLI...**:

```sh
protobot-collab init --data-dir ./team --room team --catalog ./catalog.json --password-prompt
# Alternatively: --snapshot ./room-export.json in place of --catalog.
protobot-collab serve --data-dir ./team --listen http://127.0.0.1:8787
```

`init` requires a new directory, privately stages the complete store and owner capability before renaming it into place, and refuses to overwrite existing data. `--password-file` supports noninteractive use. A snapshot starts a fresh generation/revision/history while preserving accepted part content. Omit password options for a blank-password room. See [the deployment guide](deploy/README.md) for the Linux artifact, systemd, public HTTPS and outside-network checks.

`serve --data-dir <local-room-directory> [--config <protected-bootstrap.json>] [--listen <origin>] [--public-url <https-origin>] [--allow-lan-http]` runs independently. An initialized room reopens without config. The optional public URL labels an advertised HTTPS origin in readiness, without asserting reachability. This invocation serves one room per directory; `--multi-room` selects the registry below. Legacy bootstrap initialization remains available: its JSON has `room`, `password`, a separate `ownerCredential`, the exported `catalog` and a portable `document`. Protect it; it contains credentials. Explicit `--ephemeral --config <file>` selects a temporary room; omitting both storage choices is an error. The server does not accept legacy `.pbb` bytes or remote filesystem paths. Offline backup/restore is described below.

HTTP binds to loopback by default. Public deployments require trusted HTTPS, directly through Kestrel's certificate configuration or an existing HTTPS reverse proxy on 443. For example, Caddy can forward an origin to the loopback backend:

```caddyfile
cad.example.org {
    reverse_proxy 127.0.0.1:8787
}
```

Join the HTTPS origin, not the backend URL. Direct HTTPS can use the ordinary Kestrel `Certificates:Default` configuration supplied in protected local configuration/environment. The private in-app bootstrap travels over inherited stdin, never process arguments. stdout contains a structured readiness message with the actual bound port; stderr is drained continuously. No trust bypass is installed. Arbitrary browser Origins are rejected. Failed joins, membership, write sets, messages, snapshots, event history and connections are bounded. This alpha is intended for controlled development/LAN tests; public/team service deployment acceptance remains pending.

`protobot-collab doctor --url https://cad.example.org` checks capabilities/health using normal platform TLS trust, with redirects disabled. Add `--catalog catalog.json --room team --password-prompt` for authenticated read-only checks using the same client as Unity. Default `--transport all` reports WebSocket and requests separately; `requests`, `websocket` and `auto` select individual modes. Sustained tests wait at least 21 seconds for continuing event delivery; `--quick` explicitly skips them. `--output report.json` writes a credential-free report. Exit 0 means selected checks passed, 2 means a check failed and 1 means invalid configuration. A capability-only/quick result does not establish full transport acceptance. Each transport check creates and revokes one viewer membership within the alpha's lifetime membership bound. Explicit proxy configuration, short-poll compatibility and actual target-network acceptance remain future/field work.

## Dedicated multi-room hosting

Protocol **0.9.0-alpha** adds explicit room discovery; document/SQLite/archive formats stay **v2**. Server configuration, registry header, per-room registration and server admin files each use **schema 1**. Keep the server directory on local storage under one service account. Initialize once, then start the independent service:

```sh
protobot-collab server-init --data-dir ./server
protobot-collab serve --multi-room --config ./server/server.json --listen http://127.0.0.1:8787
```

`server-init` requires a new destination and atomically publishes private `server.json`, `registry.json`, `admin.json`, `rooms/` and `staging/`. The service holds `server.lock`; each recovered room holds its own `room.lock`. Do not edit configuration identities or remove locks. Use HTTPS for public origins as in the deployment guide.

From another terminal, create and inspect rooms with the protected **server** capability:

```sh
protobot-collab room create --url http://127.0.0.1:8787 \
  --admin-file ./server/admin.json --room team \
  --catalog ./catalog.json --password-prompt --owner-output ./team-owner.json
protobot-collab room list --url http://127.0.0.1:8787 --admin-file ./server/admin.json
protobot-collab doctor --url http://127.0.0.1:8787 --room team \
  --catalog ./catalog.json --password-prompt --transport all
```

Use `--snapshot <portable-export.json>` instead of `--catalog` to seed accepted catalog/custom content. Each room can use a different matching catalog. Room IDs are case-sensitive in URLs; creation rejects case-only collisions for portable storage. Public operations accept validated content, never operator filesystem paths. The admin file must be private and unlinked (0600 on Unix; operator-only ACL on Windows), like owner files. `admin.json` can create/list rooms; it grants no room membership, asset access or participant administration. `team-owner.json` controls only that room's memberships and cannot create/list server rooms. Participant/resume credentials are also scoped to one room.

The CLI saves the newly generated owner capability to a **new private `--owner-output` file before sending**. Keep it even if creation fails or the reply is lost. Check `room list` before retrying; duplicate requests refuse overwrite, and the server retains its own `rooms/team/owner.json`. There is no destructive online deletion or server-capability retrieval endpoint.

Creation serializes one bounded admin seed at a time (72 MiB export envelope, 30-second body deadline), validates/stores/reopens the complete SQLite room, then atomically renames its directory into `rooms/<id>`. `registration.json` travels in that same rename, so there is no separate index write to partially commit. Pre-publication failures leave no entry. Startup discards recognized private interrupted staging; it never serves it. A crash after publication recovers the complete registered room. Failed post-publication opening retains the entry unavailable for operator inspection.

Root `/capabilities` and `/health` identify the multi-room listener and registry version, without a default room or catalog. Clients and owner commands use `/rooms/<id>/capabilities`; doctor with `--room` checks that room's capabilities/health. Without a selected room, doctor can check the listener only; authenticated checks require a room. Unknown IDs return `room-not-found`, unavailable registered rooms return `room-unavailable`, and neither falls back. Unity's existing Join room field/invites remain the selection mechanism. Its cache scope remains origin + room + catalog + generation.

Restart reopens every registered room independently. `room list` reports `ready` or fixed diagnostic status and recovery guidance, including locked, missing, corrupt, incompatible and identity/catalog mismatches. `/health` measures listener availability; `/rooms/<id>/health` measures the selected room. A failed room remains on disk and cannot be created over. Healthy rooms keep working. After stopping the service, preserve/quarantine the damaged `rooms/<id>` directory outside `rooms/`, then restore its archive as below. Keep every quarantined/original copy stopped.

```sh
# Stop the service first. Back up each selected room independently.
protobot-collab backup --data-dir ./server/rooms/team --output ./team-backup.zip
# Register a backup in a stopped registry with no entry for that room:
protobot-collab room restore --config ./new-server/server.json \
  --admin-file ./new-server/admin.json --input ./team-backup.zip
```

Initialize `new-server` with `server-init` first when transferring to a new host. `room restore` takes the server ownership lock and publishes a complete validated room/registration in one rename. It preserves archived room/generation/catalog, owner/password/memberships/revocations, assets, receipts and histories; it does **not** use `init` or reset identities. Duplicates by room ID, generation or owner identity are refused, including unavailable entries. A standalone `restore --data-dir <new-dir>` still creates an unregistered single-room directory. To import an existing single-room store, stop it, back it up, then use `room restore`. No live directory import or renaming of archived identities is supported. Independent registry roots cannot fence each other's copies: keep originals stopped and retain the original public endpoint/catalog for cached membership recovery.

Conservative process limits (in addition to every existing per-room limit):

| Resource | Process bound |
| --- | --- |
| Registered rooms, including unavailable entries | 8 |
| Geometry in ready loaded rooms, counting equal hashes separately per room | 128 MiB encoded; 256 MiB unique decoded-array cost; 256 MiB live-instance array cost |
| Registered retained geometry on disk | At most 512 MiB from eight 64 MiB room quotas |
| Transfers | 2 total, 1 per room, 8 MiB each, two-minute deadline; excess work is refused, not queued |
| HTTP requests / room requests | 64 total / 32 per room, including event streams |
| Event streams | 32 total / 10 per room; ordinary requests retain capacity |
| Listener connections / upgraded connections | 128 / 32 |
| Admin creation | 1 candidate; 72 MiB input; bounded per-room validation in addition to loaded-room budgets |

Budget reservations are brief accounting operations; disk I/O and transfers never hold a global edit lock. Upload and referencing-edit persistence failures roll reservations back. Capacity released by a committed deletion is published only after durable success. Startup validates one candidate at a time; validation may temporarily assemble one additional per-room bounded geometry set before admission. These are content/work bounds, not a claim about total runtime RSS, JSON object overhead or target-platform performance. Rooms exceeding process load capacity remain unavailable with `server-geometry-quota`; the operator must use a separate host or smaller rooms. There is no automatic eviction, garbage collection, history/receipt compaction, online backup, arbitrary catalog migration, broader admission policy or hosted account system.

## Offline backup and restore

Stop the dedicated server, then run the matching helper as the room's operating-system account:

```sh
protobot-collab backup --data-dir ./team --output ./team-backup.zip
protobot-collab restore --input ./team-backup.zip --data-dir ./restored-team
protobot-collab serve --data-dir ./restored-team --listen http://127.0.0.1:8787
```

Both output paths must be new; restore refuses even an empty existing directory or a dangling symlink. Store the archive outside the room directory. Backup acquires the same exclusive `room.lock` as `serve` and refuses an active room. Never remove a lock file to bypass ownership. This first version supports dedicated rooms initialized with `init`, with a matching `owner.json`; in-app recovery directories and legacy bootstrap-only directories are excluded.

SQLite's backup API captures one consistent database, including committed WAL state after a crash. The archive retains the same room/generation/catalog, accepted parts/assets/ordered mounts/chain routes, password verifier, owner capability, stable memberships/revocations, receipts and personal history/cursors. It contains no live leases or access sessions. Participant resume caches remain private files on the participants' computers. Service configuration, TLS certificates and external bootstrap files are not room data and are not included. Keep the original room stopped and point the service at the restored directory; never serve both copies of the same room. Existing participant caches can resume through the original endpoint.

Archives contain the owner credential and are unencrypted: keep them private. Unix output files use 0600 and restored directories use 0700; Windows staging and payload ACLs grant access only to the current operator. Restore under the intended service account. Source files' broader permissions and archived mode bits are never applied. Restore validates all content in a private sibling directory, opens/replays the SQLite store to validate its state and owner capability, then publishes the whole directory with an atomic, exclusive rename. Backup also publishes its completed archive without replacement. Failures clean staging and never publish a partial destination.

**Format v2:** a single-disk ZIP with exactly `manifest.json`, `room.sqlite`, and `owner.json`, without ZIP64 or an archive comment. The manifest identifies `format: "protobot-room-backup"`, `version: 2`, `storageVersion: 2`, the exact `protocolVersion` (currently `0.9.0-alpha`), room, generation, catalog hash, revision and canonical document hash. Its `files` array records each payload's name, byte length and SHA-256. Geometry BLOBs are included in the independently hashed SQLite payload. Original 0.8/v2 archives are accepted without a storage migration. Original 0.7/v1 archives are also accepted: their original state/hash is verified before atomic migration in private staging. Backing up an unmigrated v1 source writes v1 without changing the source. Other unsupported format/storage/protocol combinations are refused. The database is bounded to 1 GiB, each metadata file to 16 KiB, and the central directory to 4 KiB before ZIP entry allocation. Unknown/duplicate/missing entries, links, directory entries, traversal/absolute paths, inconsistent lengths/hashes, malformed SQLite and mismatched owner/state metadata are rejected. Integrity hashes detect damage; they do not authenticate an archive from another operator.

The lab's `BackupChecks.cs` covers real CLI round trips, committed crash-left WAL recovery, mounts/routes/geometry, receipt deduplication, persistent undo/redo and revocation, restart after restoration, exclusive ownership, overwrite and concurrent-publish refusal, Unix modes/Windows ACLs, corruption, truncation and traversal. Run the normal `dotnet run --project tools/Lab/Lab.csproj -- run --bots 10`. See the [manual acceptance checklist](ACCEPTANCE.md) for cross-platform/service checks.

## Transport and limits

This alpha uses an explicit WebSocket event stream plus ordinary POST/held GET requests, with one shared application protocol. Auto tries WebSocket with a bounded upgrade timeout and falls back to requests when upgrades are refused. Forced WebSocket and forced long-polling are available. A long poll returns immediately on an edit/preview change, or at a 20-second idle timeout; commits and disposable previews use separate requests. SignalR remains an unevaluated alternative in the original production milestone, not a second shipped implementation. Final transport/HTTPS support is gated on both actual desktop runtimes and the real deployment network.

Initial defensive alpha limits: 10 active participants, 2,000 parts, 2,000 chains, 256 immutable custom geometry assets, 256 writes/assets per transaction/lease, 8 MiB per encoded geometry asset, 64 MiB total geometry, 128 MiB unique decoded arrays plus 128 MiB summed over live instances, 16 MiB/16 unused uploads, 2 MiB reference snapshots, 4 MiB wire messages, 20,000 receipts and 64 MiB accepted-operation history. These are implementation bounds, not measured performance promises. History and referenced geometry are retained for the room's lifetime; reaching a bound refuses edits rather than evicting receipts/assets and risking broken redo or duplicate replay. Leases last 10 seconds, renew about every 3 seconds and use server-monotonic time plus an incarnation token. Preview samples coalesce at roughly 10 Hz. Durable rooms checkpoint every 64 edits or 4 MiB of journal cost and retain two complete document checkpoints. Receipts, inverse history and the journal are retained within the lifetime bounds (also 1,000 total memberships); there is no history compaction or eviction. Reaching a bound refuses new work instead of forgetting deduplication outcomes.

CI runs the .NET lab separately on macOS, Windows and Linux, and publishes self-contained helper archives. The existing Unity workflow now names the project's actual `6000.6.6f1` editor. A green generic Linux job is not evidence of Debian service acceptance. See [third-party notices](THIRD-PARTY-NOTICES.md) for redistribution requirements.
