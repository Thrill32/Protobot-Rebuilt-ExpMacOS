# Portable CLI installation and explicit updates

Published helpers include .NET. Install the complete artifact for your platform; the SDK is only needed to build from source. This workflow uses local files and directories, with no release service, background updates or service installation. Packages support macOS arm64, Windows x64, Linux x64 and Linux arm64; Windows/Linux execution remains a field gate.

## Install a local verified package

Use a complete CLI package for installation. Helpers embedded inside Unity players omit deployment templates and the installation manifest; they remain self-contained runtime support for Host/Resume.

Unpack a complete helper archive into a new directory. Obtain the expected SHA-256 of its `artifact.json` from your trusted local build/package record, separately from the candidate directory. A checksum supplied by an untrusted package does not establish authenticity. Source `publish.sh`/`publish.ps1` prints this checksum after signing and packaging; retain it with your local artifacts. Existing archives without this manifest/build inspection contract cannot be installed through this workflow; preserve them for direct/offline recovery.

```sh
./unpacked/protobot-collab install --artifact ./unpacked \
  --manifest-sha256 <TRUSTED_ARTIFACT_JSON_SHA256> --install-dir ./tools
./tools/protobot-collab build-info
./tools/protobot-collab installation-info
```

On Windows use `unpacked\protobot-collab.exe` initially and `tools\protobot-collab.cmd` afterward. Paths containing spaces are supported. Installation refuses every existing destination, including empty directories, and never changes PATH or system settings. The root launcher selects an immutable `versions/<artifact-sha256>` program directory. Package manifests cover every distributed file's path, length and SHA-256, including native libraries, helper, catalog, notices and documentation. Links inside artifacts are refused. Inputs are bounded to 2,048 entries and 1 GiB of files; metadata is at most 1 MiB. The manifest SHA-256 is the installation ID; `build-info` separately identifies actual application assemblies, source catalog, protocol, capabilities and readable formats. Documentation-only updates can have a new installation ID with the same application build.

## Set up an isolated profile

`setup` asks for hosting kind and creation policy when omitted in an interactive terminal. Interactive single-room setup also asks “Allow other editors to replace this room’s project?” (No by default), then prompts for an optional password without echoing it. Noninteractive room setup accepts `--allow-editor-project-replacement yes|no`, default No. This is persisted room policy, independent of the portable project. Supply kind and policy explicitly for repeatable noninteractive server setup:

```sh
./tools/protobot-collab setup --profile lab --kind server \
  --creation-policy key --state-dir ./lab-state
./tools/protobot-collab start --profile lab --listen http://127.0.0.1:8787
```

A profile has three independent directories under its state directory: `data` for the initialized registry or room, `participant` for a Unity `--collab-profile`, and `private` for operator-managed TLS/proxy input. `server.json`, `admin.json` and optional `creator.json` are generated inside `data`. Keep credentials out of commands, environment variables and logs. For direct HTTPS, edit the existing [private TLS format](TLS.md) under `private` and pass its path to `start --tls-file`; outbound commands continue taking their separate [private network file](NETWORK.md).

State defaults to `tools/state/<profile>` or an explicit new directory outside program/profile-record directories. The installation retains a schema-1 profile record containing only name, hosting kind and absolute state path. At most 32 profiles are supported. Profile name and state directory must both be new. Setup stages complete initialization before publication; it never runs init over existing data. If a disk failure occurs after state publication but before profile-record publication, retain and inspect the new state rather than rerunning initialization there.

For a single room, omit `--snapshot` to initialize empty content from the installed catalog, or use a validated portable export:

```sh
./tools/protobot-collab setup --profile team --kind room --room team \
  --snapshot ./room-export.json --password-prompt --state-dir ./team-state
./tools/protobot-collab start --profile team --listen http://127.0.0.1:8787
```

Shared-server profiles begin empty; use existing [Create room and owner/admin flows](ROOM_MANAGEMENT.md). `start` preserves ordinary readiness, Ctrl+C/SIGTERM, shutdown and recovery. Participant saves, membership caches and owner records remain independent. Use `--collab-profile /absolute/lab-state/participant` when launching Unity; CLI installation does not change Unity's ordinary user profile. Preserve each participant's entire private profile when moving it.

## Update and choose a previous installation

Stop managed servers, then explicitly select a complete verified local package:

```sh
./tools/protobot-collab update --artifact ./next-unpacked \
  --manifest-sha256 <TRUSTED_NEXT_ARTIFACT_JSON_SHA256>
./tools/protobot-collab installation-info
./tools/protobot-collab select
# Or choose any retained compatible ID shown by installation-info:
./tools/protobot-collab select --version <INSTALLED_ARTIFACT_SHA256>
```

`select` without an ID chooses the previous installation; every selection retains the installation just left as previous. No version is deleted automatically. Use the stable launcher after switching. Direct version paths are for inspection, not ordinary hosting. Advanced calls from an unpacked helper may supply `--install-dir` to `update`, `select` and `installation-info`.

Updates copy into private staging, verify all copied bytes, run bounded candidate `build-info`, inspect all registered profiles and atomically replace the small `active` pointer after validation. Failed validation leaves selection/data intact. Interrupted staging is never selected or automatically cleaned. If version publication succeeded but pointer switching failed, `select --version` can validate it again. No automatic update/rollback replay occurs.

Compatibility covers actual application/catalog identities, capabilities, profile format, strict config field support, registry identity/version and every room's document/SQLite versions plus validated recovered content/owner identity. Stopped-data inspection uses read-only SQLite without migration or registry staging cleanup, retaining ordinary ownership locks through selection. Existing room catalogs are preserved even when different from the new package catalog; clients still need the matching catalog for those rooms. Same protocol is insufficient: pre-creation 0.9 helpers refuse creation-policy config fields despite config schema still being 1. Helpers without verifiable build/format support are refused. Unsupported/corrupt/unavailable profiles prevent switching; inspect and repair or restore them deliberately first.

Managed `start` holds a shared installation lifetime lock; updates/selections require exclusive ownership. Directly launched servers are caught by registry/room ownership locks during inspection. Direct launches outside this workflow must remain stopped throughout switching; the manager cannot coordinate an unrelated process starting after inspection. Running executables and durable data are never replaced. The first explicit online replacement writes SQLite-v3 data and requires room-replacement-v1 support; selecting a pre-replacement helper is refused before switching. An enabled editor replacement policy additionally requires `editor-room-replacement-v1`; selection refuses older helpers even for unreplaced v2 rooms. Owner-only unreplaced v2 rooms remain eligible for a compatible previous helper. Rollback never reverses a storage/config migration: preserve original compatible programs/configs/backups and restore into a new directory when formats are incompatible. Participant profiles and private TLS/proxy files remain separate.

Useful refusal codes include `installation-manifest-hash-mismatch`, `installation-artifact-integrity-failed`, `installation-probe-unsupported`, `installation-data-or-config-incompatible`, `installation-profile-incompatible`, `installation-in-use-stop-managed-servers`, `server-data-in-use` and `room-data-in-use`. `installation-version-exists-use-select` means that installation ID is already retained. `installation-changed-run-launcher-again` means activation raced a launcher that had read the old pointer; invoke the stable launcher again.

Arbitrary existing directories are not adopted as managed profiles. Existing direct `serve`, private config and offline backup/restore remain usable; preserve their paths and original binaries. Service installation, deployment and production-server changes are separate operator actions.
