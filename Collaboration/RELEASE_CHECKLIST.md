# Collaboration release candidate: two-person acceptance

Unity **6000.6.9f1** package set: use the six archives identified in [the upgrade evidence](UNITY_6000_6_9.json), under `Collaboration/artifacts/unity-6000.6.9f1/`. The source base is `c98e9feb14801af26c0bb700bf0fea175568c86e` on `network-collab-support`, with the editor/build, documentation and CI changes described in [the acceptance record](ACCEPTANCE.md).

The earlier `18b87b0` candidate and its Unity 6000.6.6f1 package hashes remain in [the historical RC record](RC_18b87b0.json). Record the exact package set used for each acceptance run.
These are development/alpha players. Running this checklist does not authorize publication,
deployment, service installation, or changes to an existing production installation.

Every unchecked item below is **pending human/field acceptance**, even if a related local
automated test passed. A pass applies only to the recorded OS, package, transport and network.

## Set up the run

Person A operates the disposable server and owns a test room. Person B joins as an editor,
then tests a separate viewer membership. A server admin/creation key is a separate authority;
never use it as a room-owner key. Swap A/B desktop roles for the cross-platform run.

Use new directories and two independent private participant profiles, plus a separate
expendable B-viewer profile for viewer/removal checks. Keep normal user
profiles, installed releases and production endpoints out of the run. Keep the original test
data stopped when testing a restored copy at the same endpoint. Store credentials privately;
share sanitized diagnostics, artifact hashes and observations, never private recovery/owner,
creator/admin, password, proxy or TLS files.

Before starting, fill in this sheet (one copy per matrix row):

| Run field | Value |
| --- | --- |
| Date/time and A/B testers | |
| A/B OS version, CPU architecture and account type | |
| Server OS/architecture, filesystem and service account (if applicable) | |
| Helper/player archive names and SHA-256; helper artifact.json SHA-256/build ID | |
| Catalog hash on server, A and B | |
| Isolated install/state/profile/save directories (private record) | |
| Test origin/room; transport and network/proxy route (sanitized) | |
| Initial room identity/generation/revision/document hash; A/B membership IDs | |
| Per-step result, actual observation, screenshot/log/diagnostic reference, defect ID | |
| Final decision, outstanding gates and both testers' names | |

Minimum execution matrix:

| Environment | Required work | Status |
| --- | --- | --- |
| macOS arm64, two people/two machines | All steps, actual pointer/keyboard and native dialogs | Pending |
| Windows x64, ordinary account | Clean extraction/install/launcher/ACL, both desktop roles, native dialogs/update/restart | Pending |
| Linux x64 server | CLI installation/permissions, sustained doctor, restart/backup/restore/update | Pending |
| Linux arm64 Debian/Pi server | Same CLI checks plus systemd stop/start/reboot and measured resource behavior | Pending |
| Mac ↔ Windows, both hosting/owner roles | Editing, independent saves, replacement/resync, reconnect, focus and clipboard | Pending |
| Cross-OS backup archives | Mac → Windows/Linux and Windows → Mac/Linux; include a replaced-v3 room | Pending |
| Two clients outside server LAN | Trusted public HTTPS, intended port, real proxy/school route, sustained transports | Pending |

Use WebSocket on A and held requests on B for the first pass. Repeat join/edit/save,
disconnect/reconnect, replacement/resync and restart with B on **short-polling**. Confirm the
reported active transport. Exercise Auto with WebSocket deliberately blocked on a controlled
test proxy. Auto falls back to held requests; select short-polling explicitly when needed.

## Execute together

### 1. Clean installation and first launch

- [ ] A and B verify archive checksums against the retained acceptance record before extraction.
  Verify `artifact.json` against its separately retained checksum. Install the **complete CLI
  artifact**, not the helper inside a player. Follow [INSTALLATION.md](INSTALLATION.md) using
  new paths containing spaces and an ordinary account, without a .NET SDK/runtime installed.
- [ ] A runs `build-info` / `installation-info` through the stable launcher, then `setup` with
  a new server profile/state directory and explicit test creation policy. Verify separate data,
  participant and private directories, private permissions (0700/0600 or operator-only Windows
  ACLs), immutable version directory and no PATH/system changes. Existing install/state/profile
  destinations must refuse without changing their bytes. Never print keys in the evidence.
- [ ] Both launch the matching development player with distinct absolute `--collab-profile`
  directories. Verify **Collaboration** beside the filename at normal/small window sizes.
  Close/Escape returns focus to scene tools; text entry does not trigger editing shortcuts.
  Record actual downloaded/quarantined Mac launch and Windows extraction/security prompts;
  local code-signature verification alone does not establish OS download acceptance.

### 2. Create, join and separate roles

- [ ] A starts only the test server. Create a room from a small accepted local project containing
  catalog parts, a custom model with mounts and an explicit sprocket chain. Cancel creation once:
  local scene, filename, saved bytes and save marker remain intact. Confirm a fresh creation;
  **Owned rooms** retains the original origin and ownership. Check room before Join as owner.
- [ ] B joins through a password-free invite (supply password separately if configured). Both
  see the same accepted content/revision and live counts 1 → 2. Bad password, wrong catalog and
  wrong room refuse clearly and preserve the previous local project. Cancel a slow join and retry.
- [ ] B separately joins as viewer using the expendable profile: navigation/local save work, shared edits/owner controls refuse.
  Test default admin-only and keyed creation on separate disposable server profiles; explicit
  open creation is optional. Creator and master admin must not gain another room's owner controls.
  Never change a production server's creation policy for this test.

### 3. Edit, contend and save independently

- [ ] Alternate A/B placement, paint/properties, G/R, handles, Move/G→H/Connect, Alt/P copy,
  clipboard/build import, custom mount add/move/depth and explicit chain routing. Cancel a preview:
  it stays private and never appears in an accepted save. A/B accepted edits converge; viewer
  stays read-only. Save/inspect a custom model after moving/removing its original source file.
- [ ] Both attempt the same part, then different parts. Contention/stale edits refuse atomically;
  different-part edits converge. Exercise each person's Undo/Redo; neither undoes the other's
  later work. Private simulation stays local until Set pose; a conflicting shared edit cancels it.
- [ ] A saves as `A-before.pbb`, B as `B-before.pbb`, using actual native Save As panels. Cancel
  each panel once, then save. Record file hashes/paths and save markers. Make a shared edit and
  save only A: B's file bytes/path must stay unchanged. Then save B. Compare accepted content via
  diagnostics/document hash and reopened files; do not require independently serialized `.pbb`
  bytes to be identical. Verify custom geometry, mounts and chains in both saved files.
- [ ] Test native Open/Save As/import/export cancellation, focus/keyboard return, overwrite
  confirmation and unsaved-change prompts on each desktop OS. Open/New follows deliberate
  Leave/offline handling and never replaces online content. Reopen saved files in an offline
  test profile and verify them without altering the connected participants.

### 4. Owner replacement and explicit resynchronization

- [ ] Keep B connected. A leaves the shared session deliberately, opens a visibly different
  local candidate, and selects **Owned rooms → Replace online room with this project…**.
  Verify original origin/room, online revision and frozen candidate counts. Cancel/Close/Escape
  each once; online content and all local file bytes/path/camera/save state remain unchanged.
- [ ] A opens confirmation; B commits another edit; A confirms the now-stale target. Expect a
  conflict and no replacement. Inspect afresh and explicitly confirm. Exactly one new generation
  appears at the next room revision; room identity/catalog/owner and memberships remain intact.
- [ ] B pauses with replaced-room feedback and retains the last accepted scene. B chooses
  **Resynchronize replaced room…**, cancels a slow geometry hydration, and verifies old scene,
  file bytes/path/camera/save marker remain. B explicitly retries and receives the entire new
  generation. Personal undo starts empty; prior pending outcomes resolve without command replay.
  A's replacement leaves its local project open; Join as owner is a separate explicit transition.
- [ ] In a controlled disposable network interruption after confirmation, use **Check replacement
  outcome** against the retained original attempt. Record accepted/rejected/unconfirmed honestly;
  do not resend an ambiguous operation. B must never see duplicated edits. Reconnect/restart and
  check that the outcome remains discoverable. CLI equivalent: [ROOM_REPLACEMENT.md](ROOM_REPLACEMENT.md).

### 5. Disconnect, restart and administration

- [ ] Interrupt each selected transport during an unconfirmed preview and after an edit
  confirmation. Reconnect explicitly using the same profile. Accepted content and pending
  receipt IDs survive; a confirmed edit is applied at most once; no mutation is silently replayed.
- [ ] Stop/restart the test helper cleanly, then separately simulate an abrupt helper death.
  Both clients keep accepted content and pause. Resume/reconnect to the same endpoint/profile,
  verify room identity/current generation, custom geometry, memberships, receipts and new personal
  history. Restart both applications without using Leave; stable membership resumes. Test deliberate
  Leave separately because it revokes that membership. Exercise local Host/Resume independently
  from dedicated-server recovery; retain a separate local recovery test profile.
- [ ] A removes B's expendable viewer membership, verifies edit/lease/ghost termination and protected owner
  memberships, then restarts. That revoked membership stays revoked. A fresh password-based join
  is allowed and has a new identity; membership removal is not a person ban.

### 6. Offline backup and restore

- [ ] Stop the disposable server. Back up each initialized CLI room using `backup --data-dir
  <room-directory> --output <new-private-archive>`. Include a replaced-v3 room. Retain matching
  binaries/catalogs, registry/config/TLS and participant profiles separately; room archives do not
  contain an entire installation. Backup while the room is active must refuse.
- [ ] Restore a standalone room with `restore --input <archive> --data-dir <new-directory>`.
  For multi-room recovery, use a **new disposable registry** and `room restore --config
  <new-server.json> --admin-file <private-admin.json> --input <archive>`. A duplicate room or
  existing standalone destination must refuse unchanged. Inspect restored permissions before
  serving. See [offline backup/restore](README.md#offline-backup-and-restore).
- [ ] Keep the original stopped, serve the restored copy at the original test endpoint, and
  reconnect both original participant profiles. Compare accepted document hash/generation,
  geometry/mounts/routes, pending receipts, revocation and post-replacement history. Check the
  retained replacement outcome. Repeat with cross-OS archives on the matrix hosts.

### 7. Explicit update and compatible rollback

- [ ] Use a second independently verified, compatible helper artifact with a distinct application
  build and a **matching desktop/catalog strategy**. If none exists, mark this step pending; a
  documentation-only change does not prove an application update. Record both IDs/checksums.
- [ ] With only the test installation, attempt update while managed hosting is running; expect
  refusal and unchanged selection/data. Stop it, explicitly update, inspect active/previous IDs,
  restart and reconnect A/B. Profiles, private inputs, accepted content, memberships/receipts,
  geometry and saves survive. Preserve older room catalogs; never rewrite them to force a join.
- [ ] Stop and select a retained compatible previous version; repeat inspection/restart. Test a
  corrupted/wrong-digest copy: refusal must preserve selection/data. A helper that cannot read
  SQLite-v3 replacement data must refuse rollback even if its protocol is also 0.9. Restore an
  original compatible backup into a new directory for a deliberate older-format recovery;
  rollback never reverses a migration. Test Windows `.cmd` quoting/ACLs and Linux launch modes.

### 8. Public network and service gate

- [ ] With separately authorized disposable infrastructure, join from outside the LAN through
  a trusted public HTTPS endpoint on the intended port. Run `doctor` without `--quick` for every
  advertised transport and keep both people editing/idle/reconnecting. Record duration/latency,
  explicit ports, route and observed transport. Test real proxy/Basic/CONNECT and school routing
  where applicable, Auto fallback, explicit short-polling, and untrusted/name-mismatched TLS refusal.
- [ ] On disposable Debian/Pi infrastructure, verify architecture, service account/permissions,
  systemd start/stop/reboot, sustained clients, process/storage/resource observations, backup/restore
  and update. Local loopback TLS/proxy tests and cross-built binaries cannot close this gate.

## Decide and retain evidence

For every checkbox record **Pass / Fail / Blocked / Not run**, actual observations and evidence;
leave unsupported environments pending. Retain failed attempts. Both people sign the run sheet.
Any content loss, duplicated edit, incorrect authority, silent replacement/replay, changed local
save on cancellation, broken recovery, or unsafe update/restore is a release blocker. Fix and
rerun its focused regression plus affected shared paths, then identify the new artifact hashes.

Do not declare release acceptance while mandatory matrix rows remain pending. Publication,
deployment and production installation changes require separate authorization after review.
