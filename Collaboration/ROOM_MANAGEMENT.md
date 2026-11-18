# Shared-server room creation

Open **Collaboration** beside the filename in the top bar, enter a server hostname and choose **Connect / refresh**. Bare names use HTTPS; explicit schemes and ports are preserved. Select a discovered room, enter its password if required and choose **Join room**. If discovery is private or unsupported, use **Join by room ID** or **Paste invite**. Joining retains normal Save / Discard handling and starts with Save As. Normal certificate validation, explicit LAN HTTP and the Advanced network route still apply.

The last server and up to eight endpoints are remembered. **Add server** opens an endpoint field; **Create room** copies the current accepted project into a new room on the selected server; **Host locally** starts the existing local helper. Password/creation/admin forms open beside the room browser on wide windows; compact windows show one panel with **Back to rooms**. **Owner / recovery** holds secondary private ownership and local-host recovery. **Advanced / debugging** retains the detailed Host/Join/Local rooms/Create/Owned rooms/Server admin interface and transport/proxy/replacement controls. Development players expose the entry normally; ordinary release builds still require the collaboration switch. Main.unity is unchanged.

After joining a durable room, the profile's protected `Collaboration/sessions` recovery file retains a reusable membership credential, rather than the password. It is scoped to canonical server origin, exact room ID, catalog and stable room identity. Rejoining preserves the existing role/history, including viewer roles. **Forget saved access** removes only that participant record; it refuses while unresolved edit IDs still need recovery. It does not revoke the server membership or delete private local/created owner records. Explicit Leave retains its existing revocation behavior. Secrets are separate from ordinary endpoint preferences, invites, project files and admin/creation keys. Recovery files use atomic private writes (Unix 0700 directories/0600 files; Windows inherits the user profile ACL). No OS keychain or encryption-at-rest claim is made.

Creation retains private owner recovery before sending and does not replace an online room or open it locally: it creates a new generation/history from current accepted catalog/custom content. Your project/file stay open. **Allow other editors to replace this room’s project?** is an explicit **No / Yes** setting, default No. Join as owner afterward through Owner / recovery. Existing replacement confirmation, outcome Check and explicit resynchronization rules are unchanged.

## Public room directory

Discovery is independent of creation and administration. Existing and new servers default to **hidden**. To opt in when initializing:

```sh
protobot-collab server-init --data-dir ./server --directory-policy public
```

To change an existing server, stop it and authenticate with its separate admin file:

```sh
protobot-collab server-directory --config ./server/server.json --admin-file ./server/admin.json --directory-policy public
# Use --directory-policy hidden to disable discovery again.
```

Public `GET /rooms` returns directory version/server identity and at most eight healthy available rooms, each containing only `room` and `passwordRequired`. No generations, catalog hashes, revisions, users/counts, diagnostics or credentials appear. There is no room-name search, pagination, background indexing or unbounded enumeration. The client accepts at most 4 KiB. Hidden lookup refuses; unavailable registrations stay visible only to administrators for repair. Server request limits and no-store policy apply. A public listing never joins a room, tests a password, supplies ownership or authorizes creation/admin access. Public policy exposes every healthy registered room ID; operators needing an unlisted server must keep it hidden. There is no per-room visibility setting in this version.

Root capabilities add optional `roomDirectoryVersion: 1` and `roomDirectoryPolicy`. Old helpers and dedicated single-room hosts need no directory update for direct ID joining. New helpers read absent visibility as hidden. Config schema 1 adds optional `directoryPolicy`; strict older helpers refuse a config containing that field. Preserve the original config/binary for deliberate rollback. Offline creation-policy changes preserve visibility, and directory changes preserve creator/admin credentials. Installation selection checks the supported config-field list.

## Operator policy

Initialize a new dedicated server with exactly one policy. The default remains `admin-only`:

```sh
protobot-collab server-init --data-dir ./server
# Or generate a separate private creator.json alongside admin.json:
protobot-collab server-init --data-dir ./keyed-server --creation-policy key
# Or deliberately allow anyone reaching the listener to create a room:
protobot-collab server-init --data-dir ./open-server --creation-policy open
protobot-collab serve --multi-room --config ./keyed-server/server.json --listen http://127.0.0.1:8787
```

For an existing server, stop its process first and use the separate admin file. Key mode generates a fresh creation key at a **new** private destination, rotating any old creation key. Room identities/content/owners and participant credentials remain intact:

```sh
protobot-collab server-policy --config ./server/server.json --admin-file ./server/admin.json --creation-policy key --creator-output ./server/creator-next.json
protobot-collab server-policy --config ./server/server.json --admin-file ./server/admin.json --creation-policy open
protobot-collab server-policy --config ./server/server.json --admin-file ./server/admin.json --creation-policy admin-only
```

The registry ownership lock refuses changes while a server is running. Existing destinations are never overwritten for a generated key. If a config-write result is uncertain, retain the generated key and inspect the stopped config; no update is automatically resent. `creator.json` uses `format: protobot-room-creator`, `schema: 1`, `serverId` and a random 256-bit hex `credential`; `server.json` stores only its SHA-256 hash. Disabling or rotating creation authority does not revoke ownership of already created rooms. Keep retired creation files private, or remove them deliberately after checking the new policy.

| Authority | Supported rights |
| --- | --- |
| Server administrator | List registered rooms/status and process/room resources; create rooms under every policy; stopped-server policy configuration and offline restore |
| Creation key | Create new rooms only when the operator selects `key`; no directory, global diagnostics, participant management or ownership of other rooms |
| Explicit open creation | Create a new privately owned room without a creation credential when the operator selects `open`; same limits and no directory |
| Room owner capability / owner membership | Join and manage that room's participants and inspect its usage; no global server rights |
| Room password | Admit a new editor/viewer; blank permits password-free joining of an existing room |
| Participant access / resume credential | Existing room-scoped membership and personal history; no creation or server authority |

All creation paths retain the eight-room process limit, including unavailable registrations, one creation permit, 72 MiB input/30-second upload deadline, atomic directory publication, existing geometry/process budgets, request admission and per-room limits. Open mode makes these bounded slots self-service; it adds no hosted accounts, billing, public relay, room directory, identity quota or abuse-prevention service. Operate the listener on an appropriate network. HTTPS/platform name validation and the existing private outbound proxy policy apply unchanged.

## Create inside Protobot

Host and Create room offer **“Allow other editors to replace this room’s project?”**, a Yes/No room policy defaulting to **No**. Yes permits current authenticated editor memberships to explicitly replace; viewers stay read-only. The setting belongs to the room and survives restart, backup/restore and project replacement independently of uploaded content. Existing rooms remain owner-only unless deliberately configured. CLI `init`, `room create` and single-room `setup` accept `--allow-editor-project-replacement yes|no`; protected initial Bootstrap configuration uses `allowEditorProjectReplacement`. See [replacement policy, participant path and offline configuration](ROOM_REPLACEMENT.md).

Enter the server origin, room ID, name and optional room password. Check the server policy, select **Room creation key**, **Explicitly open server** or **Server administrator**, and enter the corresponding 64-character credential in the masked field when required. Checking a policy clears private keys; enter the key afterward. Confirm **Create new room** to publish the current accepted content. Key/password input stays in memory and clears after the operation or closing the window. Nothing copies it to invites, arguments, preferences, exports or logs.

Before the single creation POST, Protobot durably retains a fresh owner credential in the profile's private `Collaboration/created-rooms` directory. Its bounded `protobot-created-room` schema-1 records hold only origin, server ID, room/generation/catalog scope, ownership and outcome flags (4 KiB each; 32 records). A private file lock serializes preparation/publication/outcome updates across windows sharing a profile, including corrected-key confirmations; it never replaces in-flight ownership. Owner participant recovery uses a separate scoped sidecar; ordinary editor/viewer caches are untouched. Preserve this profile to retain ownership; the room password or creation key cannot recover a lost owner capability.

Successful creation opens **Owned rooms**, where private prepared/created records expose **Check room**, **Join as owner** and the separately confirmed replacement action. A canceled or failed preparation can be checked there without resending. **Local rooms** instead resumes an editor-owned helper from this computer.

**Check room** verifies the server identity, room generation/catalog and owner-authenticated diagnostics without creating a membership. A canceled request, lost reply or ambiguous storage failure retains an unconfirmed preparation; use Check or Join as owner instead of resending creation. Duplicate preparations refuse a second mutation. Only a recognized definitive pre-publication authorization refusal marks a preparation as refused, permitting a subsequent explicit Create confirmation after correcting the key. Unreadable records are retained and shown as unreadable; no retry is inferred from them. Existing rooms are never overwritten.

Creation and server inspection are available while outside a connected room. Cancel/Close/Escape cancels ongoing network work and preserves local content/files and existing membership caches/pending IDs. Check/Join controls refer to each record's original origin rather than a subsequently edited server field. Owner Join uses the selected network route and normal trust checks; only the editor-owned loopback helper is forced Direct.

The **Server admin** page requires the separate administrator key to inspect existing supported room-list/status/resource operations, including unavailable rooms. Inspection creates no membership and exposes no owner credentials. **Create a room as administrator** opens the creation form with admin authority selected. Room-owner participant controls remain on the connected owner session page. Owner-authorized replacement is available separately in private owned-room records; see [replacement controls and recovery](ROOM_REPLACEMENT.md). Live deletion, role/password changes and universal owner impersonation remain unavailable.

## CLI and compatibility

The existing admin create/list commands remain available. Delegated/open creation uses a protected key file or an explicit flag:

```sh
protobot-collab room create --url https://cad.example.org --creation-key-file ./private/creator.json --room team --snapshot ./room-export.json --owner-output ./private/team-owner.json --password-prompt
protobot-collab room create --url https://cad.example.org --open-creation --room open-team --catalog ./catalog.json --owner-output ./private/open-team-owner.json
```

Use exactly one of `--admin-file`, `--creation-key-file` and `--open-creation`. Keys/passwords are private file or masked interactive inputs, never argument/environment values. The CLI retains its owner output before sending and never retries creation. Owner output uses the existing owner-file format and can be used by the existing participant/diagnostics commands. Keep it after a lost reply; query that exact room with its owner file. No room-list access is needed for recovery.

Root capabilities add optional `roomCreationVersion: 1`, `roomCreationPolicy` and `serverId`. `POST /rooms` authorizes the selected creation policy before parsing a bootstrap; it shares the existing registry creation path. `/admin/rooms` (GET/POST) and `/admin/diagnostics` remain master-admin-only. The separate operator-enabled `GET /rooms` directory described above grants no administrator authority. Room-specific capabilities do not advertise global creation authority. New UI/delegated CLI operations refuse old helpers before preparing owner credentials or sending a mutation; existing Host/Join/admin CLI work remains compatible with older supported helpers. An original pre-creation helper fixture is exercised in the focused lab.

Wire remains **0.9**, documents **v2**, registry/admin **v1**. SQLite/archives remain **v2** until an explicit replacement atomically writes **v3**; older helpers refuse replaced data. Config schema 1 adds optional `creationPolicy`/`creationKeyHash`; absent policy means admin-only. Old helpers' strict config readers refuse files with these new fields: retain original config and compatible binaries for a deliberate rollback, and never run an old helper against a policy it does not understand. Existing room catalogs retain their original matching scope; no automatic catalog rewrite occurs.

Validation entry points: `RoomCreationChecks.cs --room-creation-only`, the full lab, and `ProtobotEditorTools.CollaborationRoomCreationValidation.Smoke` with a fresh profile. Local tests do not establish Windows/Linux/service/public-network field acceptance. See [validation](../VALIDATION.md) and [acceptance](ACCEPTANCE.md).

## Larger custom-model compatibility

For 64 MiB encoded custom models, update the helper and every player. Capability `geometryLimitsVersion: 1` / `geometryAssetBytes: 67108864` is checked before admission; an older player is refused rather than allowed into a room it cannot hydrate. A new player connected to an older helper retains that server's 8 MiB limit. Total encoded room geometry remains 64 MiB and decoded/live-instance budgets remain 128 MiB. No ownership, authentication or storage format changes are required. Existing rooms/catalogs can remain intact; preserve a stopped backup before retaining larger assets because an older helper cannot read them. See [large-model support](LARGE_MODELS.md).
