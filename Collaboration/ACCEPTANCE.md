# Collaboration 0.8 manual acceptance

Use one matching catalog/helper/player release on every desktop and server. Record OS, transport, network, artifact checksum and result for each run. Local automated checks do not complete these field checks.

Pending field acceptance remains a release requirement; it does not block implementing and locally validating the next bounded collaboration feature.

Automated content-addressed geometry pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned .NET SDK 10.0.403):

| Check | Result |
| --- | --- |
| CLI build | Passed, zero warnings/errors; cached packages with NuGet online audit disabled in this environment |
| Published Mac CLI | Passed init → backup → restore → backup; v2 manifest verified; original 0.7 refuses v2 archives without publishing a destination |
| Expanded .NET lab, both transports, asset quotas/failures/restart/backup/restore | Passed, 630 assertions, 10 bots per transport, including two freshly rebuilt Mac players |
| Original 0.7 executable / v1 room and backup migration | Passed forward migration, original hash checks, injected rollback, owner/stable identity/password/revocation/receipt/redo/mount/route preservation |
| Unity collaboration smoke | Passed real 90,000-vertex OBJ above 4 MiB encoded, remote materialization, copies/mount history, accepted self-contained local Save and portable export; explicit original 0.7 catalog |
| Unity recovery smoke | Passed large geometry over private bootstrap stdin, real helper kill/restart, rehydration, persistent identity/history, independent local file and removed-cache rejection |
| Mac / Windows development players, four helpers and six archives | Rebuilt; packaged/embedded catalogs and managed helper assemblies verified; Mac signature and two-player runtime checks passed |
| Catalog | `97cc2730f7863f9b91f7d62f5d58e409709e5929bdf375e200648ad66192c313` |

Protocol is **0.8.0-alpha**, document/SQLite/archive formats are **v2**. Original 0.7/v1 rooms and backups upgrade atomically; old clients/helpers cannot open v2. Keep original private backups for rollback and use the original matching catalog/cache scope (`--collab-catalog` on upgraded Unity) for existing rooms. The separate transfer caps are 8 MiB/asset, 64 MiB retained bytes, 128 MiB unique decoded arrays plus 128 MiB summed over live instances, 16 MiB/16 unused uploads and 256 total assets. Ordinary 2 MiB metadata snapshots and 4 MiB messages remain unchanged. The measured test mesh is 5,426,656 bytes, 90,000 vertices and 30,000 triangles. A conservative full-lab memory measurement before asset-metadata indexing was 683,474,944 bytes maximum RSS and 205,423,600 bytes peak footprint; this is local evidence, not a target-platform performance promise.

- [ ] **Large geometry field acceptance:** Mac ↔ Windows import/remote mesh/mount/copy/history/accepted Save/export, slow/interrupted and revoked transfers, quotas, matching original-catalog room upgrades, and cross-OS v1/v2 recovery archives. Verify Debian/Pi service stop/restore/restart and actual HTTPS/proxy transfer behavior. Local automated results do not establish these checks.

Credential-free summaries/checksums are under ignored `Collaboration/artifacts/geometry-report.json` and `geometry-packages.json`. No commit or deployment was performed. The Notes update remains in the repository’s existing ignored local Notes directory.

The administration/stabilization results below are **previous records**, not tests rerun or evidence of 0.8 field acceptance.

Automated participant-administration pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned .NET SDK 10.0.403):

| Check | Result |
| --- | --- |
| CLI build | Passed, zero warnings/errors |
| .NET lab, both transports, restart/backup/restore and two fresh Mac players | Passed, 505 assertions, 10 bots per transport |
| Unity collaboration, UI and recovery smokes | Passed; pointer refresh/remove/cancel/confirm, owner protection, terminal Unity WebSocket feedback, earlier 0.7 capabilities, removed cached identity after helper restart |
| Original 0.7 helper/archive compatibility | Passed in both directions; updated owner removal stays revoked after original-helper restore; new clients connect to the old helper on both transports and refuse unavailable administration |
| Mac / Windows development players and four helpers | Rebuilt; Windows/Linux execution remains pending |
| Catalog | `639c1bf272e1984e5d36ddf6fac36dd4ffc22aba63a93a44f9a163af5ccd936f` |

Protocol remains `0.7.0-alpha`, SQLite schema and backup format remain v1. The new capability is optional; existing room/backup data needs no migration. The conservative catalog fingerprint changed, so keep matching original catalog/players for existing rooms; CLI management/backup use the stored catalog. This pass establishes local automated behavior, not deployment acceptance. Credential-free artifact/checksum evidence is recorded under ignored `Collaboration/artifacts/participant-administration-report.json`.

The November 14 results below remain the previous stabilization record; they were not all rerun for this administration pass.

Automated stabilization on November 14, 2026 (macOS arm64, Unity 6000.6.6f1, .NET SDK 10.0.403):

| Check | Result |
| --- | --- |
| Source catalog | `69aeae8ded4766f0042f186746de14fdbee37d01ee39be1d804fb1b0fe730879` |
| .NET lab, both transports, backup/restore and two built Mac players | Passed, 394 assertions, 10 bots per transport |
| Unity collaboration, simulation, recovery and UI smokes | Passed |
| Chain routing smoke and chain contour validation | Passed |
| Input/settings/camera/box dispatch and rendered probes | Passed; camera at both sizes and selection images inspected |
| Mac / Windows development players | Both rebuilt; Mac signature/runtime passed; Windows execution pending |
| Four helper archives and two player ZIPs | Regenerated; every checksum, packaged catalog and managed helper assembly verified |
| Published Mac helper init / backup / restore / backup | Passed |

Generated distributions and the credential-free evidence/checksum report are under ignored `Collaboration/artifacts/` (`stabilization-report.json`). No deployment or Windows/Linux execution is established by these local results. The unchanged protocol remains `0.7.0-alpha`.

- [ ] **Participant administration:** on Mac ↔ Windows and the dedicated Debian/Pi service, remove active and inactive editor/viewer memberships by stable ID on both transports. Check owner-only controls, confirmation/cancel, protected owners, terminal feedback, lease/ghost cleanup, retained accepted content and unaffected peer undo/redo. Verify private owner-file permissions and credential-free CLI output; retry a lost response, reconnect/restart, then back up/restore and confirm the old resume credential stays revoked. Verify separately that knowing the room password still permits a fresh membership. Windows ACL execution, Linux service restoration and real proxy/network connection termination remain pending.

- [ ] **Mac ↔ Windows editing:** run two independent profiles; host/join as owner, editor and viewer. Exercise catalog/custom import, paint/default, G/R, handles, Move/G→H/Connect, box selection, camera/settings, Alt/P/clipboard/build import, deletion and personal undo/redo. Confirm equal accepted documents and independent Save As paths on both transports; verify Windows focus, modifiers and borderless window behavior.
- [ ] **Debian / Raspberry Pi:** verify `uname -m` against linux-x64 or linux-arm64 and the release checksum/catalog. Initialize a private room as the service account, start through systemd, run authenticated sustained `doctor` checks on both transports, stop/restart and reboot. Confirm service ownership, restrictive storage permissions and retained accepted content/history.
- [ ] **Public HTTPS:** use a trusted certificate and the intended public port/network. Join from both desktop OSes outside the host LAN; run sustained WebSocket and request checks through Caddy. Verify Auto fallback when upgrades are blocked, continuing idle events and certificate rejection for untrusted/name-mismatched endpoints.
- [ ] **Mount editing:** import a custom model; add/delete/move/rotate/copy points, change group depth and recalibrate grid. Confirm previews/canceled edits stay private and out of saves; confirm accepted edits arrive in place, contention is refused, undo/redo works, and layouts survive save/reload and server restart.
- [ ] **Explicit chains:** use Shift+C / Chain route, change cyclic order and inner/outer wraps, then Apply/Cancel. Verify contour and signed simulation from both clients; Alt/P/clipboard/build import remap full routes; deleting an endpoint removes its chain atomically and undo restores its identity/order/wraps.
- [ ] **Disconnect / recovery:** interrupt each transport during previews and after confirmation; reconnect and resolve pending receipts without duplicate edits. Kill/restart the dedicated server, resume both stable identities and personal history, verify revoked membership stays revoked, and compare accepted saves. Exercise deliberate Leave and Continue offline separately.
- [ ] **Offline backup / restore:** stop the service, create a private archive as its account, restore to a new directory, verify 0700/0600 (or Windows operator-only ACLs), switch the service to that directory and keep the original stopped. Rejoin both identities through the original endpoint; compare accepted content, mounts/routes, saved receipts and undo/redo. Confirm active-room backup and existing-destination restore are refused. Exercise Mac-created archive → Windows/Linux and Windows-created archive → Mac/Linux with the same release.
