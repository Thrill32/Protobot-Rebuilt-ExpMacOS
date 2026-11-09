# Collaboration development alpha

This implementation prioritizes Host/Join and everyday catalog editing. It is opt-in and is **not the completed collaboration release** described in the local `/Notes` plans. Rooms are ephemeral: save the accepted document to a local `.pbb` before stopping the host. Server restart recovery, durable identity/history, assets and complete tool coverage remain release gates.

The same self-contained `protobot-collab` executable serves an editor-owned room and a standalone room. Unity and the bots compile the **same** protocol, reducer and client sources in `Packages/com.protobot.collaboration/Runtime`. There is no Unity server, BinaryFormatter network payload, hosted account or mandatory relay.

## Try Host/Join

1. Install the SDK pinned by `global.json`. From `Collaboration`, run `bash publish.sh osx-arm64` on a Mac, or `./publish.ps1 win-x64` in PowerShell on Windows. Publish each matching helper before making its player build. Generated helpers and players stay in ignored `artifacts/`.
2. In Unity, use **Tools → Protobot → Collaboration → Export Catalog**, then **Enable Development UI**, and enter Play Mode. This installs the controller at runtime; `Assets/Scenes/Main.unity` is unchanged.
3. Open **Collaboration · development alpha**, enter a name, room ID and optional password, and select **Host current build**. Port zero chooses an available port. Loopback allows another process on the same computer. For a second LAN computer, explicitly enable LAN HTTP and use a reachable LAN address/port; firewall/reachability is untested until that computer connects.
4. In the second instance, use the same catalog/build, enter that server origin, room ID and password separately, and select **Join room…**. Existing unsaved changes use the ordinary Save/Discard flow. A newly joined document starts with Save As; hosting preserves the host's path.

Use the regular add menu with hole/hardware/gear snapping, G/R, transform handles, Move (including G → H), Connect, paint/Default, existing-part properties and Delete. Alt copies commit movement and new parts together. P stamps frozen selections; clipboard paste and build import accept catalog-only content. Targeted operations carry the versions read while resolving geometry and reject if those targets change. Leases cover the complete frozen write set. A waiting reservation is shown in the collaboration status. Other participants see cyan mesh ghosts during G/R; cancellation removes them. Incoming poses are resolved results, not mouse gestures replayed on another client. Accepted poses retain selection, and deletion clears invalid selection. Custom geometry, mounting tools, chains and simulation remain gated; see [mutation coverage](MUTATION_COVERAGE.md).

Undo/Redo applies to your accepted edits in the current room membership. The authority supplies inverse content, checks live reservations and refuses an inverse after another participant touches any affected part. Deletion undo restores the same stable IDs with newer versions; copy-and-move undo is atomic. Reconnect preserves this in-memory history, while leaving or stopping the room ends it. Offline history is separate.

When updating this alpha, use the same source/catalog on every computer and **republish the helper on each host**; an old helper cannot accept the new commands. Run the publish command in step 1 and Export Catalog again before testing. Restart existing rooms after updating. The user reported basic G/R, add and delete working across two Windows editor runtimes plus one macOS editor runtime on November 8, 2026; expanded tools still need that manual cross-platform pass.

Each participant saves the current **accepted** document using their own camera, path and recent history. Save waits for that participant's submitted edits, excludes previews, and leaves later remote changes dirty. Disconnection freezes shared edits. Reconnect resolves pending receipts and loads a fresh consistent snapshot; it never automatically resubmits an unknown operation. Leave/Continue offline forks the last accepted document. Open/New leaves the room after local unsaved handling. A host's quit/crash closes its private pipe and ends only its owned helper; independent servers are unaffected.

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

On macOS use the executable inside `Contents/MacOS`. A profile owns a file lock and its own atomic JSON preference store covering settings, rebinding, recent builds and default directories. Profile initialization errors fail rather than falling back to ordinary user preferences. Credentials are currently memory-only; no resume credentials or cache files are written. The clipboard remains the real system clipboard. Recognized option values are excluded from startup `.pbb` file scanning. Never pass passwords or tokens as arguments.

## Fast validation

No Unity license is needed for the protocol/server lab:

```sh
cd Collaboration
dotnet build src/Cli/Cli.csproj
dotnet run --project tools/Lab/Lab.csproj -- run --bots 10
dotnet run --project tools/Lab/Lab.csproj -- run --bots 2 --transport long-polling
dotnet run --project tools/Lab/Lab.csproj -- run --bots 10 --player artifacts/players/Protobot.app
```

The lab checks model atomicity, numeric/catalog validation, independent expected poses, canonical hashes, disjoint edits, contention, fake-clock lease fencing, viewer/owner/password handling, duplicate operations, ghost cancellation, delete/tombstones, paint/replacement transactions, atomic transform-copy history, sequential personal undo/redo and stale inverse refusal, explicit reconnect, blocked-upgrade Auto fallback, lost acknowledgments, broadcast-before-ack, snapshot handoff during active edits and actual CLI EOF shutdown. Optional `--player` launches two built development players with independent profiles, the real in-app helper, WebSocket and forced request transports; compares their canonical documents and independently checks scene poses/colliders/save snapshots. It terminates only processes it starts. Every run prints its isolated diagnostic directory with credential-free reports; generated run artifacts are retained.

Unity integration smoke (omit `-quit`; the test exits itself):

```sh
Unity -batchmode -nographics -projectPath /absolute/project \
  -executeMethod ProtobotEditorTools.CollaborationValidation.Smoke \
  --collab-profile "/tmp/protobot-collaboration-unity-profile" \
  -logFile /tmp/protobot-collaboration-smoke.log
```

This exercises the managed host launcher, Unity's actual networking runtime, catalog generation/materialization, paint/state preservation, G/R commit/cancel boundaries, accepted saves, remote deletion, path isolation and Host/Join/leave. It also drives paint/Default, property regeneration/selection, personal history, G → H, Move, pointer handles, Connect, Alt copies, P and clipboard paste. Batch Mode invokes gesture boundaries explicitly because it has no focused game window. Existing keyboard-transform, Escape, selection, build-file and transfer suites remain the input/offline regressions. Windows/Linux script compilation uses `ProtobotEditorTools.FilePickerPlatformValidation.CompilePlayers`. Cross-OS manual operation, trusted HTTPS/proxy inspection, actual school-network compatibility and Debian service operation must still be verified before release.

## Standalone server

`serve --config <protected-bootstrap.json> [--listen <origin>] [--allow-lan-http]` runs the same executable independently. The bootstrap JSON has `room`, `password`, a separate `ownerCredential`, the exported `catalog` object and a portable `document` object (schema 1, matching catalog hash, stable generation, revision, parts and tombstones). Protect this configuration; it contains credentials. The server does not accept legacy `.pbb` bytes or client filesystem paths. CLI room administration, portable export UI and backup/restore are not implemented yet.

HTTP binds to loopback by default. Public deployments require trusted HTTPS, directly through Kestrel's certificate configuration or an existing HTTPS reverse proxy on 443. For example, Caddy can forward an origin to the loopback backend:

```caddyfile
cad.example.org {
    reverse_proxy 127.0.0.1:8787
}
```

Join the HTTPS origin, not the backend URL. Direct HTTPS can use the ordinary Kestrel `Certificates:Default` configuration supplied in protected local configuration/environment. The private in-app bootstrap travels over inherited stdin, never process arguments. stdout contains a structured readiness message with the actual bound port; stderr is drained continuously. No trust bypass is installed. Arbitrary browser Origins are rejected. Failed joins, membership, write sets, messages, snapshots, event history and connections are bounded. This alpha is intended for controlled development/LAN tests; persistent public/team deployment belongs to the later recovery/admin milestones.

`protobot-collab doctor --url https://cad.example.org` checks an ordinary capability request using platform TLS trust. It reports that transport and actual-network acceptance remain separate checks; it is not the comprehensive school-network doctor proposed in `/Notes`.

## Transport and limits

This alpha uses an explicit WebSocket event stream plus ordinary POST/held GET requests, with one shared application protocol. Auto tries WebSocket with a bounded upgrade timeout and falls back to requests when upgrades are refused. Forced WebSocket and forced long-polling are available. A long poll returns immediately on an edit/preview change, or at a 20-second idle timeout; commits and disposable previews use separate requests. SignalR remains an unevaluated alternative in the original production milestone, not a second shipped implementation. Final transport/HTTPS support is gated on both actual desktop runtimes and the real deployment network.

Initial defensive alpha limits: 10 active participants, 2,000 parts, 256 writes per transaction/lease, 2 MiB accepted snapshots, 4 MiB wire messages, 20,000 receipts and 64 MiB accepted-operation history. These are implementation bounds, not measured performance promises. History is retained for the room's lifetime; reaching its bound refuses edits rather than evicting receipts and risking duplicate replay. Leases last 10 seconds, renew about every 3 seconds and use server-monotonic time plus an incarnation token. Preview samples coalesce at roughly 10 Hz. No persistence/checkpoint compaction is claimed.

CI runs the .NET lab separately on macOS, Windows and Linux, and publishes self-contained helper archives. The existing Unity workflow now names the project's actual `6000.6.6f1` editor. A green generic Linux job is not evidence of Debian service acceptance. See [third-party notices](THIRD-PARTY-NOTICES.md) for redistribution requirements.
