# Everyday collaboration navigation validation

Work began from clean `e4ddc1d` (`network-collab-support`), including the persisted editor replacement policy. `Assets/Scenes/Main.unity` is unchanged. Validation uses Unity **6000.6.9f1**, the pinned .NET **10.0.403** SDK, disposable profiles and loopback servers. Historical release records and player packages are retained.

## Implemented contract

The top-bar popover remembers normalized server endpoints, discovers rooms only when the operator permits it, and supports password or saved-membership joining. Bare hostnames use HTTPS; explicit schemes/ports retain existing TLS, LAN and proxy rules. The room list scrolls; forms appear beside it on wide windows and replace it with Back/Close below 1100 physical pixels. Add server, Create room and Host locally have separate actions. Private owner/recovery and admin controls remain secondary, and Advanced/debugging retains the detailed interface. Creation includes the existing default-No editor replacement policy.

Only endpoints enter preferences. Rejoining uses the existing private server/room/catalog/identity-scoped participant recovery credential. Forget preserves owner records and refuses to discard unresolved edit IDs. Discovery lists at most eight healthy rooms' IDs/password-required flags, with a 4 KiB client bound. Missing visibility policy means hidden; public listing supplies neither membership nor administrative authority. See [operator controls and storage contracts](ROOM_MANAGEMENT.md).

## Recorded checks

| Check | Result | Retained evidence |
| --- | --- | --- |
| CLI / Lab compilation | Passed, zero warnings/errors | Pinned SDK, cached package flags |
| Full ten-client, three-transport lab | Passed, 1,751 assertions, 295.97 s | `/private/tmp/protobot-navigation-full-lab-01.log`; temporary result directory `protobot-collab-21ad9804d1534c079ed1aff58dcbb8c0` |
| Directory / saved access | Passed, 35 assertions | `/private/tmp/protobot-navigation-directory-final.log`; `protobot-collab-18be66c2d4a44ca18f1d805d617e54d2` |
| Installation / rollback | Passed, 39 assertions | `/private/tmp/protobot-navigation-installation-final.log`; `protobot-collab-717589df80c14f0da0b68aadd58d94fc` |
| Rendered navigation | Passed, 130 navigation assertions plus full browser scenario | `/private/tmp/protobot-navigation-release-render.log`; `/private/tmp/protobot-navigation-release-render/navigation-render` |
| Unity regressions | All seven passed: core, UI, connection, recovery, creation, replacement (81 assertions), multi-room | `artifacts/navigation-unity-results.json`; `/private/tmp/protobot-navigation-regression-*.log` |
| Rendered independent Mac players | Passed: actual pointer local hosting/direct-ID joining, owner + viewer, WebSocket, four inspected renders | `/private/tmp/protobot-navigation-players-final-03/summary.json` |
| Built-player scene convergence | Passed, eight assertions across WebSocket and short-polling pairs | `/private/tmp/protobot-navigation-player-lab-final.log` |
| Four self-contained helpers / two desktop players | Rebuilt; manifests, embedded catalogs, managed assemblies, guides, actual engine versions and Mac signatures verified | [machine-readable package evidence](NAVIGATION_VALIDATION.json) |

The full lab used original 0.7, 0.8 and pre-diagnostics 0.9 fixtures, plus the retained pre-creation, pre-replacement and pre-directory helpers. It covers role authority, replacement/resynchronization, persistence/backup, admission/cancellation, geometry, receipts, proxy and local TLS behavior. The focused directory suite also verifies exact 4096/4097-byte boundaries, too many/duplicate rooms, wrong server identity, hidden/absent policy, unavailable registrations, no admission/admin fallback, role-preserving credential rejoin, cross-server isolation, pending-safe Forget and direct joining against the original pre-directory helper.

The rendered scenario drives Main's actual EventSystem with pointer, wheel, Tab and Escape input: top-bar opening, room selection/password, outside-click dismissal, Save/Discard before joining, independent viewer presence, saved rejoin/Forget, creation/admin forms and compact fallback at 1280×720, 960×640 and 800×600. Screenshots were inspected. Earlier failed attempts are retained. They exposed and led to fixes for a reparented ScrollRect transition and pointer targets moving during keyboard-focus scrolling; the Advanced owner-management regression passed after the pointer correction.

## Final artifacts and field limits

The [machine-readable evidence](NAVIGATION_VALIDATION.json) records final catalog/build/manifest/assembly hashes and all suite results. Helpers are under `artifacts/osx-arm64`, `win-x64`, `linux-x64`, `linux-arm64`. New players preserve retained player paths:

- [Mac player](artifacts/navigation-20261118/players/Protobot.app)
- [Windows player](artifacts/navigation-20261118/players/Windows/Protobot.exe)

The original native helper/catalog remain in `/private/tmp/protobot-navigation-baseline-e4ddc1d`; the other three original helpers are preserved in verified tar archives there. Complete full-lab and installation fixtures were verified and compressed under `/private/tmp/protobot-navigation-validation-fixtures`, with their summaries/archive pointers retained at the original result paths. Earlier failed render/player attempts remain available. The standalone pointer probe now waits for initial layout and clicks the viewer checkbox's actual hit graphic.

The final viewer's reusable recovery file was independently checked for a 0700 containing directory and 0600 file, with its token absent from preferences and logs. Unrelated font/player-setting reserialization was restored after building. `git diff --check` passed and Main.unity remains untouched.

Local automation does not establish Windows/Linux/Pi execution, cross-OS editing, native file-picker behavior, a deployed public HTTPS/proxy network, or two-person release acceptance. Mac signing is local ad-hoc integrity, not notarization/Gatekeeper acceptance. Credential protection uses private recovery files (Unix 0700/0600; Windows profile ACL inheritance), not an OS keychain or encryption-at-rest claim. Public discovery is server-wide, without per-room visibility overrides; operators needing unlisted rooms must retain hidden policy. Cached builds disabled the unavailable online NuGet audit. Existing room catalogs are not migrated; their matching build/catalog or the existing explicit catalog override remains required. Historical release archives were not repackaged.
