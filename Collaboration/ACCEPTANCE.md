# Collaboration 0.9 manual acceptance

Use one matching catalog/helper/player release on every desktop and server. Record OS, transport, network, artifact checksum and result for each run. Local automated checks do not complete these field checks.

Pending field acceptance remains a release requirement; it does not block implementing and locally validating the next bounded collaboration feature.

Automated bounded admission/backoff pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403 / runtime 10.0.14; base `33a38c9`):

| Check | Result |
| --- | --- |
| CLI / Lab builds | Passed with zero warnings/errors; cached packages with `NuGetAudit=false`; no online vulnerability audit |
| Final full lab | **1,236 assertions in 143.34 seconds**, ten bots on each of WebSocket, held requests and short polling; three-transport multi-room/limits/geometry/restart/restore and actual original 0.7/v1, 0.8/v2 and pre-diagnostics 0.9/v2 fixtures |
| Final focused admission/backoff suite | **127 assertions** with the rebuilt Mac CLI: deterministic jitter/hint clamps, three-send serial bounds, real twelve-second overall/request cancellation deadlines, terminal typed HTTP/auth/TLS/malformed failures, all routes/transports, canceled wait/request/transfer/pending recovery, single-send lost join/commit/create replies, room/process exhaustion and fairness, saturating rate/full guidance, actual rebuilt Mac CLI safe reads and doctor stop/cooldown behavior |
| Actual Unity controls / regressions | Connection and proxy waiting/exhaustion/cooldown, pointer Cancel/Close and physical Escape passed with real geometry, private input and accepted scene/file/cache/history isolation; ordinary UI, core editing/save/export, simulation, recovery and multi-room passed with fresh profiles |
| UI smoke observation | Initial participant-removal pointer confirmation timed out; a fresh isolated rerun passed the unchanged assertions. The failed attempt is retained in evidence; no inferred cause or field focus acceptance is claimed |
| Rebuilt Mac player pairs | **8 assertions**, four processes, WebSocket↔held requests and WebSocket↔short polling: scene poses/colliders, independent accepted saves/profiles and convergence |
| Builds / distributions | Four helpers, Mac/Windows players and six archives rebuilt; matching packaged/embedded catalogs and managed assemblies, SHA-256 manifests, Mac helper/app signatures and an extracted Mac archive signature verified |

Safe retries are explicitly scoped to capability/health and operator GETs (**3 sends / 12 seconds total / 4 seconds per request**) and immutable geometry (**3 sends / 120 seconds total**). Safe waits use 250/500 ms plus 0–250 ms jitter and clamp recognized hints to 250–5,000 ms. Whole shared-client connection attempts have a **180-second** deadline, with up to ten additional seconds only to settle an already-sent admission; owned-helper startup remains separately bounded to thirty seconds. Admission, edits/history and admin mutations remain single-send. Failed admission stops further doctor transport admissions. Event loss still requires explicit reconnect/receipt inspection, without application replay or implicit route bypass. Manual full/rate/request cooldowns are bounded to 250–60,000 ms per selected address/room. Existing request/transfer/room/membership limits remain unchanged; no IP/identity queue/map or forwarded-address trust is added. See the [exact operation matrix and limits](NETWORK.md#admission-feedback-and-bounded-safe-retries).

Standard optional `Retry-After` and fixed HEAD rejection headers need no capability/configuration/storage migration. Original 0.9 helpers use bounded local defaults; the actual old full-room rejection and existing absent-capability workflows passed. Protected CLI reads retain their numeric HTTP error projection and fixed administration codes. Protocol stays **0.9.0-alpha**, document/SQLite/archive **v2**, server config/registry/admin **v1**, diagnostics **v1**, local network config **schema 1**. Catalog: `44ee44968cf57de685f8f849ef597f796b208581e28731439a3ceb5d61de1671`. Keep original matching catalogs for existing rooms; no arbitrary catalog migration occurs.

- [ ] **Admission/backoff field acceptance:** Windows/Linux execution; Mac↔Windows editing/transfers/cancellation/focus; native file panels; cross-OS archives; Debian/Pi service restoration/resource behavior; trusted public HTTPS/proxy/school-network acceptance. Local loopback/proxy/untrusted-TLS fixtures establish none of these field results. PAC/SOCKS/integrated authentication, certificate provisioning, accounts, GC/compaction, online backup, live destructive deletion and arbitrary catalog migration stay separate.

Credential-free evidence is under ignored `Collaboration/artifacts/admission-report.json`, `admission-fixtures.json`, `admission-unity.json`, `admission-builds.json` and `admission-packages.json`. The matching pre-change helper/catalog was preserved under new `/private/tmp/protobot-admission-legacy-09` and `/private/tmp/protobot-admission-original-catalog.json`; original fixture directories were retained and actual versions plus distinct managed helper hashes verified. Main.unity was retained; only inspected generated font/default-backend serialization was removed. This validation preceded the user-requested local commit; no push/deployment was performed. Previous proxy/short-poll/diagnostic evidence below remains historical and was preserved.

Automated explicit outbound-proxy pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403 / runtime 10.0.14; base `de0d5f6`):

| Check | Result |
| --- | --- |
| CLI / Lab builds | Passed with zero warnings/errors; cached packages with `NuGetAudit=false`; no online vulnerability audit |
| Final full lab | **1,107 assertions**, **108.65 seconds**, ten bots on each of WebSocket, held requests and short polling; three-transport multi-room/restart/restore; verified original 0.7/v1, 0.8/v2 and pre-diagnostics 0.9/v2 compatibility fixtures |
| Focused proxy suite (shared clients + rebuilt Mac CLI) | **81 assertions**: real private configuration, anonymous/Basic forwarding, all-mode doctor, owner/admin operations, HTTP forwarding/HTTPS CONNECT, TLS rejection, origin-auth confinement, incorrect/unsupported auth, malicious reflected/encoded/large 407 errors, route/generation fencing, cancellation, geometry and receipt recovery without replay |
| Actual Unity proxy controls | Passed anonymous/Basic runtime transports and authenticated CONNECT/TLS rejection; private input, sustained connection test, Join/Cancel/Escape/pause/route/reconnect; real geometry bytes/materialization, unchanged accepted scene/local files/membership and direct owned-helper recovery |
| Unity regressions | UI (including actual older-helper diagnostic decoding/resource/export), ordinary connection cancellation, core editing/save/export, simulation, recovery and multi-room passed with fresh profiles |
| Rebuilt Mac player pairs | Both WebSocket↔held-request and WebSocket↔short-poll pairs passed (**8 assertions**, four player processes): scene poses/colliders, independent accepted saves/profiles and convergence |
| Distribution | Four helpers, Mac/Windows players and six archives rebuilt; matching embedded/packaged catalogs and managed assemblies, archive checksums, Mac helper/app signatures and an extracted Mac archive signature verified |

Implemented policy is local System/Direct/HTTP-proxy with anonymous or Basic authentication and HTTPS CONNECT. Proxy input is separate from room/owner/admin credentials. Unity input stays in memory; CLI uses a private bounded **`protobot-network` schema-1** file. Owned-helper loopback stays direct. All HTTP paths and WebSocket upgrades use a per-client redirect-disabled handler; runtime WebSocket framing and Auto fallback semantics remain. Same-client route changes preserve original origin/room/catalog/generation membership, accepted content, pending IDs and local files. Diagnostic schema 1 adds only **optional** safe route/authentication enums; the ordinary UI regression caught and verified the missing-field compatibility fix. Wire stays **0.9**, room/SQLite/archive **v2**, server config/registry/admin **v1**. Current catalog: `7f96031e8c255cbda689c11ff15a0da9274b025fc21a6b5583f4eaa92b22c853`. Exact mechanisms, fixed codes and limitations: [outbound network configuration](NETWORK.md).

- [ ] **Proxy field acceptance:** Windows/Linux execution; Mac↔Windows editing/transfers/cancellation/focus; native file-panel behavior; cross-OS archives; Debian/Pi service restoration/resource behavior; trusted public HTTPS/proxy/school-network acceptance. Local CONNECT and untrusted-TLS fixtures do not establish trusted public deployment/network acceptance. Explicit HTTPS-to-proxy, PAC, SOCKS, Digest/integrated authentication and certificate provisioning remain unsupported/separate; tested OS defaults may continue platform behavior.

Current credential-free evidence is under ignored `Collaboration/artifacts/proxy-report.json`, `proxy-unity.json`, `proxy-publish.json`, `proxy-builds.json` and `proxy-packages.json`. The matching pre-proxy helper/catalog was preserved before publication. Main.unity is unchanged; only inspected Unity-generated font/default-setting serialization was removed. No commit, push or deployment was performed. Earlier records below are historical results, not new runs inferred from this pass.

Automated short-poll compatibility pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403; after user-requested diagnostic commit `a150ca0`):

| Check | Result |
| --- | --- |
| CLI / Lab builds | Passed with zero warnings/errors; cached packages with `NuGetAudit=false`; no online vulnerability audit established |
| Focused short-poll iteration | Passed **39 assertions**, including a real held-request-blocking intermediary, immediate/strict/authenticated reads, idle pacing, geometry references/hydration, lost acknowledgment/pending receipt resolution, cancellation, revocation, real CLI doctor and ten-bot convergence; final full run additionally covers unchanged Auto behavior and default CLI three-mode discovery |
| Full collaboration lab | Passed **1,031 assertions** in **97.65 seconds**, ten bots on each of WebSocket, long polling and short polling; three-transport multi-room isolation/transfer/quota/restart/restore; original 0.7/v1, 0.8/v2 and 0.9/v2 fixtures; real old-helper pre-admission/cache preservation and two-mode default doctor |
| Actual Unity controls / regressions | UI short-mode selection, sustained diagnostic probe, editing/materialization/accepted save/reconnect/leave passed; real-byte connection progress/Cancel/Escape passed in short and existing request modes; core editing/save/export, recovery and multi-room regressions passed with fresh profiles |
| Rebuilt Mac player pairs | Both WebSocket↔long-poll and WebSocket↔short-poll Host/Join pairs passed independent scene poses/colliders/save snapshots/profiles; four player processes total |
| Published Mac CLI | Explicit short-mode and default three-mode doctor passed; existing protected owner/admin diagnostics passed |
| Builds / archives | Four helpers, Mac/Windows players and six archives rebuilt; packaged/embedded catalogs, helper/player managed assemblies, archive checksums and Mac signatures verified |

`shortPollingVersion: 1` is optional and required before admission. The existing authenticated cursor/one-event batch gains strict `wait=0`; ordinary held reads are unchanged. Clients have one event read in flight, delay at least 250 ms after an edit batch or 1,000 ms after idle data, and cancel requests/delays through the existing lifetime. Auto stays WebSocket → held requests. Short mode trades slower event/preview delivery and higher idle HTTP traffic for intermediary compatibility; these are client pacing bounds, not a new server admission policy or a target-platform performance promise. Explicit reconnect resolves pending receipts without replay. Wire stays **0.9**, document/SQLite/archive **v2**, configuration/registry/admin **v1**, diagnostics **v1**. Current catalog: `df012c739791fc53ecc1fde072166c9454170f82b60a76701f4b66a4ebd9dfeb` (the committed diagnostics helper/catalog was preserved before rebuilding).

- [ ] **Short-poll field acceptance:** Windows/Linux execution; Mac↔Windows interaction/geometry/reconnect/cancellation/focus; cross-OS archives; Debian/Pi stopped-service restoration and observed HTTP/resource load; actual trusted public HTTPS/proxy/school-network behavior. General admission policy, GC/compaction, online backup and destructive live deletion remain separate work. No pending field check was closed by local automation.

Current credential-free evidence is in ignored `Collaboration/artifacts/short-polling-report.json`, `short-polling-unity.json`, `short-polling-builds.json`, `short-polling-published-cli.json` and `short-polling-packages.json`. The earlier diagnostics slice was locally committed at the user's request as `a150ca0`; this validation preceded the user-requested short-poll commit. No push or deployment was performed. `Assets/Scenes/Main.unity` was retained, and only verified Unity-generated font/default-setting churn was removed. Notes/artifacts stay ignored; earlier validation records below are historical.

Automated bounded-diagnostics/resource pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403; base `1f403f3`):

| Check | Result |
| --- | --- |
| CLI / Lab builds | Passed with zero warnings/errors; cached packages and `NuGetAudit=false`; online vulnerability audit unavailable |
| Focused diagnostics and current compatibility | Passed **72 assertions**: version/byte/event bounds; reflected malicious errors and credential/path/payload exclusion; owner/admin/participant/cross-room scopes; exact retained/decoded/live/unused/history/receipt quotas; exclusive export/cancel/failure; frozen, locked, corrupt, incompatible and process-over-budget status beside healthy editing; actual CLI commands; real original 0.9 helper/backup and absent-capability export |
| Full collaboration lab | Passed **903 assertions**, **69.13 seconds**, ten bots per transport, original 0.7/v1 + 0.8/v2 + 0.9/v2 fixtures and two freshly rebuilt Mac players; includes unresolved-pending-operation export without receipt resolution/replay and real active transfer/event gauges |
| Actual Unity controls | Passed pointer resource refresh/export/Cancel, native-panel cancellation callbacks, collaboration-window physical Escape, existing-file failure and unchanged accepted scene/local file bytes/saved status/membership/connection; native OS panel focus/keys are not established by the injected dialog wrapper |
| Unity regressions | UI, connection cancellation/progress, editing/import/materialization/Save/portable export, recovery/helper restart and multi-room smokes passed with fresh profiles |
| Published Mac CLI | Passed two-room creation/listing, scoped owner/admin diagnostics, versioned doctor, stopped backup and offline registry restore |
| Builds / distribution verification | Four helpers, Mac/Windows players and six archives rebuilt; embedded/packaged catalogs, diagnostic managed assemblies, archive hashes and Mac signatures verified (see ignored package report) |

The new diagnostic format is `protobot-diagnostics` **schema 1** (64 KiB; eight rooms; 32 fixed-size recent events), with optional `diagnosticsVersion: 1`. Doctor adds `protobot-connection-report` schema 1 while retaining existing fields/exit codes. Wire stays **0.9.0-alpha**, document/SQLite/archive **v2**, configuration/registry/registration/admin **v1**. Original 0.9 helper/catalog was preserved before publication at a private temporary location; old helpers without diagnostics still join/export local metadata. Matching release catalog is `7e344d8590e5a1f2c83291e5990848a1511d6e6eb4859e19758c262f0fa62ba5`. Existing rooms keep their original catalog scope; no arbitrary migration was introduced.

Reports expose logical encoded/decoded/live-instance geometry costs separately from admin-only measured managed/working-set memory. Gauges are sampled, not a globally atomic snapshot or runtime memory guarantee. Unavailable registrations have null metrics/revision and fixed actionable status; collection never implicitly opens, initializes, migrates, repairs or replaces their data. General GC, compaction, online backup and destructive live deletion remain separate work.

- [ ] **Diagnostics field acceptance:** Windows/Linux execution and permissions; Mac↔Windows usage/exports/editing/transfers/cancellation/focus; real native save-panel Escape/focus; cross-OS archives; Debian/Pi service restart/restore and observed resource behavior; trusted public HTTPS/proxy access and failures. These remain pending alongside earlier field gates.

Current credential-free evidence is under ignored `Collaboration/artifacts/diagnostics-report.json`, `diagnostics-unity.json`, `diagnostics-builds.json`, `diagnostics-published-cli.json` and `diagnostics-packages.json`. The production `Assets/Scenes/Main.unity` was retained; Unity-generated font/default-setting serialization was removed after inspection. No commit, push or deployment was performed. Notes and generated artifacts remain intentionally ignored. Earlier connection/multi-room/geometry results below are historical, not tests rerun by inference.

Automated desktop connection-polish pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403):

| Check | Result |
| --- | --- |
| CLI build | Passed with zero warnings/errors; cached packages with `NuGetAudit=false`; no current online vulnerability audit claimed |
| Focused connection lifecycle lab | Passed 25 assertions: cooperative bootstrap encoding cancellation, pre-cancel/admission reply settlement, partial body bytes and retry attempt progress, same-identity retry, overlapping join refusal, unchanged accepted snapshot/pending receipt IDs and personal history |
| Full collaboration lab | Passed **828 assertions**, 10 bots on each transport, original 0.7/v1 + 0.8/v2 fixtures, and two freshly rebuilt Mac players |
| Unity connection smoke | Passed actual pointer Cancel and physical Escape during real geometry downloads, visible count/MiB progress, retained scenes/local saved bytes, same-membership retry, stale-progress isolation and canceled owned Host/Resume cleanup/retry |
| Unity UI / collaboration / recovery / multi-room regressions | Passed modal/diagnostic/owner/viewer/export controls, editing/import/materialization/Save/export, helper kill/restart/history/revocation and separate rooms; core/recovery/multi-room used the preserved original 0.9 catalog scope |
| Distribution artifacts | Four helpers, Mac/Windows players and six archives rebuilt; packaged/embedded catalogs, managed assemblies, SHA-256 files and Mac signatures verified |
| Catalog | `dfd530294ce2f576ddaebdcb4915f19ea842d9b6970902955bc17c98ed260453` |

This is local-only progress and cancellation behavior: protocol remains **0.9.0-alpha**, document/SQLite/archive **v2**, config/registry/registration **v1**. A candidate snapshot is hydrated and pending outcomes resolved before publication; the previous accepted snapshot survives canceled reconnect. Returned membership/sequence credentials are saved before honoring cancellation, with a ten-second admission reply bound. Cancellation retains known membership and never replays an ambiguous edit. Startup serializes on a worker with cancellation checks between output buffers, a thirty-second deadline and ownership-scoped process cleanup. Quotas and mutation authority are unchanged. Existing rooms still require their original matching catalog; no automatic catalog rewrite is performed.

- [ ] **Connection polish field acceptance:** actual Windows/Mac pointer, keyboard/Escape and focus behavior during slow/interrupted joins and reconnects; native helper cancellation/process cleanup; real proxy/HTTPS retries and timeouts. Existing Windows/Linux execution, cross-OS archives, Debian/Pi service restoration and Mac↔Windows field editing/transfers remain pending.

Credential-free current evidence/checksums are under ignored `Collaboration/artifacts/connection-report.json` and `connection-packages.json`. The final lab took 64.05 seconds. `Assets/Scenes/Main.unity` was retained. This validation preceded the user-requested follow-up commit; no push or deployment was performed. Notes and generated artifacts remain intentionally ignored. The earlier multi-room/geometry records below are historical, with their own catalog/checksums and assertion counts.

Automated dedicated multi-room pass on November 16, 2026 (macOS arm64, Unity 6000.6.6f1, pinned SDK 10.0.403):

| Check | Result |
| --- | --- |
| CLI build | Passed with pinned SDK, zero warnings/errors; cached packages with `NuGetAudit=false`, no online vulnerability audit established |
| Full .NET collaboration lab | Passed **803 assertions**, 10 bots per transport and two freshly rebuilt Mac players; multi-room isolation/limits/failures/restart/restore, single-room regressions and actual original 0.7/v1 + 0.8/v2 fixtures |
| Unity multi-room smoke | Passed real dedicated helper, room-ID joins on both transports, independent custom meshes and accepted local saves, scoped caches and restart |
| Unity single-room collaboration / recovery smokes | Passed real large OBJ/import/materialization/Save/export, private-stdin helper kill/restart, identities/history/revocation and independent local files, using the preserved original 0.8 catalog scope |
| Published Mac CLI | Passed two custom-geometry room creations, read-only quick doctor on both transports, offline per-room backup, registry restore and restart with unchanged room/generation/catalog |
| Distribution artifacts | Four helpers, Mac/Windows players and six archives rebuilt; packaged/embedded catalogs, managed assemblies and SHA-256 manifests verified; Mac helper/app signatures verified |
| Catalog | `68319de357050098df126047268b73747749354c409c88e009b592a1b81e59f4` |

Protocol **0.9.0-alpha**, server config/registry/registration/admin **schema 1**; document/SQLite/archive remain **v2**. Original 0.8/v2 rooms reopen without storage migration and their archives restore as standalone or registered rooms. Original 0.7/v1 forward migration remains atomic. Use matching 0.9 helper/clients and each room's original matching catalog; retain original private backups/builds for rollback. There is no automatic catalog rewrite.

Process limits: eight rooms including unavailable registrations; 128 MiB loaded encoded geometry, 256 MiB unique decoded-array cost, 256 MiB live-instance array cost; at most 512 MiB registered disk geometry from eight per-room quotas. Two transfers total/one per room; 64 requests/32 per room; 32 event streams/10 per room; 128 listener connections/32 upgrades; one 72 MiB admin seed with a 30-second body deadline. Existing room quotas and ordinary 2 MiB snapshots/4 MiB messages remain. One candidate is temporarily validated within per-room bounds before admission. These content/work limits are not a runtime RSS or Pi performance promise.

- [ ] **Multi-room field acceptance:** Windows/Linux execution and native permissions/rename; Mac↔Windows separate-room joins/editing/custom transfers/saves/reconnect; cross-OS v1/v2 per-room archives; Debian/Pi stopped-service backup, registry restore and restart; trusted public HTTPS/proxy simultaneous transfers and room diagnostics. Listener readiness does not establish healthy status for every room. Keep original/restore copies stopped except the selected service. Local tests do not establish these field results.

Credential-free results and checksums are under ignored `Collaboration/artifacts/multiroom-report.json`, `multiroom-packages.json` and `multiroom-published-cli.json`. The final lab took 63.16 seconds on this machine. The revocation test now awaits its asynchronous feedback callback instead of racing the disconnected flag; authority/peer assertions remain intact. `Assets/Scenes/Main.unity` was retained. This validation preceded the user-requested source commit; no deployment was performed. The local Notes update and generated artifacts remain intentionally ignored.

The following 0.8 geometry record is historical; its earlier assertion/player/package results are not tests run for 0.9.

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
