# Collaboration 0.7 manual acceptance

Use one matching catalog/helper/player release on every desktop and server. Record OS, transport, network, artifact checksum and result for each run. Local automated checks do not complete these field checks.

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

- [ ] **Mac ↔ Windows editing:** run two independent profiles; host/join as owner, editor and viewer. Exercise catalog/custom import, paint/default, G/R, handles, Move/G→H/Connect, box selection, camera/settings, Alt/P/clipboard/build import, deletion and personal undo/redo. Confirm equal accepted documents and independent Save As paths on both transports; verify Windows focus, modifiers and borderless window behavior.
- [ ] **Debian / Raspberry Pi:** verify `uname -m` against linux-x64 or linux-arm64 and the release checksum/catalog. Initialize a private room as the service account, start through systemd, run authenticated sustained `doctor` checks on both transports, stop/restart and reboot. Confirm service ownership, restrictive storage permissions and retained accepted content/history.
- [ ] **Public HTTPS:** use a trusted certificate and the intended public port/network. Join from both desktop OSes outside the host LAN; run sustained WebSocket and request checks through Caddy. Verify Auto fallback when upgrades are blocked, continuing idle events and certificate rejection for untrusted/name-mismatched endpoints.
- [ ] **Mount editing:** import a custom model; add/delete/move/rotate/copy points, change group depth and recalibrate grid. Confirm previews/canceled edits stay private and out of saves; confirm accepted edits arrive in place, contention is refused, undo/redo works, and layouts survive save/reload and server restart.
- [ ] **Explicit chains:** use Shift+C / Chain route, change cyclic order and inner/outer wraps, then Apply/Cancel. Verify contour and signed simulation from both clients; Alt/P/clipboard/build import remap full routes; deleting an endpoint removes its chain atomically and undo restores its identity/order/wraps.
- [ ] **Disconnect / recovery:** interrupt each transport during previews and after confirmation; reconnect and resolve pending receipts without duplicate edits. Kill/restart the dedicated server, resume both stable identities and personal history, verify revoked membership stays revoked, and compare accepted saves. Exercise deliberate Leave and Continue offline separately.
- [ ] **Offline backup / restore:** stop the service, create a private archive as its account, restore to a new directory, verify 0700/0600 (or Windows operator-only ACLs), switch the service to that directory and keep the original stopped. Rejoin both identities through the original endpoint; compare accepted content, mounts/routes, saved receipts and undo/redo. Confirm active-room backup and existing-destination restore are refused. Exercise Mac-created archive → Windows/Linux and Windows-created archive → Mac/Linux with the same release.
