# Authorized online room replacement

Development-alpha implementation contract. Local checks and separate field acceptance are recorded in [acceptance](ACCEPTANCE.md).

Replacement is a separate, explicitly confirmed operation authorized by persisted room policy. Owners may always replace. The creation setting **“Allow other editors to replace this room’s project?”** is **No** by default; **Yes** also allows authenticated, non-revoked editor memberships. Viewers remain read-only with either value. An owner membership is distinct from an ordinary editor; creation keys and server administrators gain no replacement authority. It copies the current accepted local project into an explicitly selected existing durable room. Ordinary Open/New still leaves a shared session; neither action replaces server content. Server administrators and creation keys gain no room-owner authority.

The authorized participant or owner first reads an authenticated target containing room identity, catalog, active generation, revision and document hash. Confirmation freezes that target and validated portable local content. The single mutation must match all target fields under the room gate; an intervening edit or replacement rejects it. The server accepts portable content/geometry only, never .pbb deserialization or filesystem paths. Catalog identity stays unchanged; use the matching catalog/build for an older room. Geometry, request, process and history limits still apply.

One SQLite transaction publishes a fresh document generation at the next monotonic room revision, its complete validated geometry and a retained replacement outcome. It clears prior document journal/checkpoints/personal undo history, leases and previews. Memberships, password/owner authority, revocation flags, monotonically increasing member sequences and every accepted/rejected command receipt survive. Lifetime receipt/history budgets do not reset. At most 64 replacement outcomes are retained per room; reaching the bound refuses another replacement. Lost replies are checked by operation ID, never automatically resent.

The immutable registration generation becomes the stable room identity; SQLite alone owns the active generation. The first replacement records that original identity and the complete bounded generation-transition chain in the same transaction. Registration/catalog/owner identity is never rewritten alongside the database. Restart and backup/restore validate that chain against the registration anchor. Replaced rooms use SQLite/archive v3, explicitly unreadable by older helpers; documents remain v2. Unreplaced v2 data is not silently migrated to v3. Restore preserves the anchor and outcomes.

Old access sessions, edits, leases, preview frames and transfers are fenced. An old session may still explicitly leave its own membership. Admission to replaced rooms requires stable-identity/current-generation proof; older clients refuse before creating an orphan membership. Event transports pause with `room-replaced`; connected clients keep their last accepted local scene. Only explicit resynchronization resumes the same membership, checks prior-generation pending outcomes without command replay, hydrates a complete candidate and then publishes the new accepted scene. Cancellation/failure keeps the prior accepted scene, file path/saved bytes/camera/save marker, credentials and unresolved IDs. A retained accepted old command remains accepted even though its content was superseded. Absence in the complete retained receipt set proves it was not accepted before replacement. Personal undo starts empty in the new generation.

The optional `editorRoomReplacementVersion: 1` capability advertises this permission setting; room capabilities also report `allowEditorProjectReplacement`. The boolean is optional persisted room metadata (absent means No), independent of `RoomExport` and document hashes. Restart ignores any contradictory bootstrap policy for an initialized room. Replacement copies stored policy into its atomic metadata publication; SQLite backup/restore retains it. Creation with Yes refuses helpers lacking the capability. Managed installation selection requires `editor-room-replacement-v1` when any room enables it. Use matching helpers for direct launches as well; older direct writers cannot preserve a policy they do not understand. No new document/SQLite/archive version is needed for the additive policy.

Capability `roomReplacementVersion: 1` and the stable `roomIdentity` distinguish this contract from older protocol-0.9 helpers. New participant recovery schema 2 records stable identity and the generation of pending IDs; schema-1 caches can be read and upgraded explicitly. New fields in shared serialized DTOs are optional for older decoders. Private created-room records retain their original identity/owner capability; checking ownership can report a later active generation without silently rebinding ownership to another room.

Unity controls and CLI commands are implemented. Focused local checks cover crash boundaries, conflicts, lost replies, all transports, retained outcomes and large candidate geometry. See the acceptance record for distribution and UI results; no field or release acceptance is implied.

## Use inside Protobot

Outside a connected session, open **Owned rooms** and find the room in your private owned/prepared records. Choose **Replace online room with this project…**. Inspection authenticates the original server/room identity and owner capability. Review the exact origin, room, online revision and frozen local project counts, then choose **Replace room content** or **Keep online room**. Creating another room and ordinary Open/New remain separate actions. A successful replacement keeps your local project/file open; **Join as owner** subsequently opens the current online generation through normal unsaved-project handling.

For a non-owner editor, keep the local project open and choose **Join → Replace online room with this project…**. Enter the intended origin/room and name/password, or reuse that room’s saved membership. This authenticates and durably retains the participant credential without hydrating the online project, starting an event pump, or changing the local scene/file/save state. The same target review and **Replace room content** confirmation follow. Cancel/Close/Escape preserve the local project. A saved viewer remains a viewer even if the Viewer checkbox is cleared; forgetting a saved membership is a separate explicit action. Owner records and owner keys are never consulted by this path. After replacement, normal Join opens the online content only with the existing deliberate generation/unsaved-project handling.

On Join, retained attempts for the selected origin/room offer **Check replacement outcome**. Check reauthenticates the same saved participant without opening the online project, including after the replacement fenced its old access token. Revoked memberships cannot inspect, replace or check outcomes; an owner can still inspect retained results. Both endpoint admission and final publication recheck server authority under the room gate.

Prepared replacement attempts remain in the profile's private Collaboration/room-replacements directory, separately from owner capabilities. At most 64 records of 8 KiB are retained per profile, serialized under a private profile lock. Preserve completed records privately before archiving them manually if this bound is reached. Each record identifies the exact target, local content hash and single operation; it contains no owner credential or project geometry. **Check replacement outcome** reads that original target's outcome. Close, Escape and Cancel retain an already prepared attempt and never resend it. An absent replacement result remains unconfirmed while its target is unchanged: a delayed send might still arrive. Once the authoritative target changes, that old precondition cannot publish later. A new confirmation always uses a fresh inspection/operation and retains older attempts.

Connected participants see **Resynchronize replaced room…** after the server pauses editing. Cancel keeps the last received accepted build. Confirm checks old pending outcomes and opens a complete hydrated generation. A canceled hydration or receipt check keeps all unresolved IDs and continues requiring explicit confirmation. Reopening a saved participant membership can offer **Join replaced room…** with the same deliberate confirmation. Private owner Join and locally recovered-host Resume deliberately open their current generation while retaining the stable room/owner identity. No owner key is inferred from a server-admin key.

Geometry hydration uses a separate bounded candidate cache. Old and candidate caches each retain the existing 64 MiB encoded / 128 MiB decoded-array bounds; the old accepted scene remains usable on cancellation. Only successful synchronization swaps the cache. Replacing one valid large project with another does not accumulate both against a single generation's quota.

## Creation and persisted policy

Host and Create room expose one Yes/No dropdown with the wording above. The same room-creation choice applies to both. Existing rooms and old metadata remain No; changing a creation form does not modify an existing room.

`init`, `room create` and single-room installed `setup` accept `--allow-editor-project-replacement yes|no` (omission is No). Interactive room setup asks the same question. A protected initial Bootstrap configuration/private stdin uses the boolean `allowEditorProjectReplacement`; portable project exports contain no policy. To deliberately configure an existing dedicated room, stop its server and run:

~~~sh
protobot-collab room-policy --data-dir ./server/rooms/team --allow-editor-project-replacement yes
~~~

This command requires the room’s private `owner.json` and exclusive room lock. It changes stored room policy atomically, leaving document revision/content, memberships, receipts and history intact. It also works for an initialized single-room data directory. The server has no new public policy-edit endpoint.

| Authority | No | Yes |
| --- | --- | --- |
| Room owner capability or current owner membership | Replace | Replace |
| Current authenticated editor membership | Refused | Replace |
| Viewer or revoked membership | Refused | Refused |
| Creation key or server administrator without room membership | Refused | Refused |

## CLI controls

Use a protected room owner file, the original origin and a portable accepted-content export. The target and attempt destinations must be new. The attempt's parent must be private (a missing parent is created privately); existing broader directories are refused rather than repaired.

~~~sh
protobot-collab room replacement-target --url https://cad.example.org \
  --owner-file ./lab-state/data/rooms/team/owner.json --output ./lab-state/private/target.json
protobot-collab room replace --url https://cad.example.org \
  --owner-file ./lab-state/data/rooms/team/owner.json --target ./lab-state/private/target.json \
  --snapshot ./room-export.json --attempt-output ./lab-state/private/replacement.json --confirm-room team
protobot-collab room replacement-check --url https://cad.example.org \
  --owner-file ./lab-state/data/rooms/team/owner.json --attempt ./lab-state/private/replacement.json
~~~

The private target file pins the original origin as well as the authenticated room target; an identical cloned room at another origin cannot reuse it. The exact --confirm-room must match the owner file and inspected target. The mutation is sent once; a lost reply retains the attempt for Check. A stale target refuses publication; existing target/attempt files are never overwritten by these preparation commands. Read-only Check atomically updates only its private outcome record. All three commands accept existing per-connection network-route/private-proxy options and normal HTTPS/name validation; owner credentials stay in files or existing memory, never arguments/environment/logs. The server never receives local paths.

Editors use the same three commands with participant authority in place of `--owner-file`:

~~~sh
protobot-collab room replacement-target --url https://cad.example.org \
  --participant-session ./private/editor-session.json --room team --catalog ./catalog.json \
  --name Editor --password-prompt --output ./private/target.json
protobot-collab room replace --url https://cad.example.org \
  --participant-session ./private/editor-session.json --room team --catalog ./catalog.json \
  --target ./private/target.json --snapshot ./room-export.json \
  --attempt-output ./private/replacement.json --confirm-room team
protobot-collab room replacement-check --url https://cad.example.org \
  --participant-session ./private/editor-session.json --room team --catalog ./catalog.json \
  --attempt ./private/replacement.json
~~~

The first command retains a participant resume credential in the new private session file. Later commands resume its existing role without a password or owner key. Keep this file for Check after an ambiguous outcome; existing editor/viewer session caches are also accepted with their original origin/room/catalog scope. Authentication never hydrates the online project or resolves/drops pending edit IDs. Do not use a session cache held by another running client.

Regression commands and local evidence are recorded in [validation](../VALIDATION.md). The development-only `check-editor-replacement-players.py` runs two actual desktop players through both policy values, preserving their separate local project files and testing explicit owner resynchronization after a non-owner editor replacement.
