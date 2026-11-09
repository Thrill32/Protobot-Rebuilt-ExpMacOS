# Connected-mode mutation coverage

The development switch is required. Only the rows marked supported may mutate a connected document. The offline editor retains its existing paths. This audit is a release gate, not a claim of complete collaboration coverage.

| Mutation / action | Connected alpha | Real entry point / guard |
| --- | --- | --- |
| Host current committed catalog build | Supported; no chains/custom models | `CollaborationSession.HostAsync`, `CollaborationScene.CaptureDocument` validate/normalize identities before publishing |
| Join / reconnect / remote apply | Supported | shared client snapshot/cursor + `CollaborationSession` main-thread queue; `CollaborationScene` applies stable IDs, poses, materials and detector refresh |
| Catalog placement / hole, hardware and gear snapping | Supported, one accepted create per stamp | `Placement.Place` → `PlaceCatalog`; geometry queries collect versioned read dependencies through `CollaborationScene.BeginReads/Read/EndReads`; stale targets reject atomically |
| G / R on a placed selection/assembly | Supported | `KeyboardTransformTool.BeginSelection` acquires full-set lease; `CompleteConfirmation` submits exact resolved poses; cancellation restores baseline/releases lease |
| Delete selected parts / assembly | Supported | `ObjectDeletor` → full-set reservation + atomic `DeleteParts`; tombstones prevent identity reuse |
| Save / Save As | Supported independently | `BuildsManager` waits pending barrier, captures accepted snapshot/hash and keeps each participant's path/marker |
| Open / New / leave / continue offline | Supported | normal unsaved handling, detach, accepted baseline preservation; stale async results cannot act on a new session |
| Selection / camera / grid / hole visibility | Local only | ordinary inputs remain available; selection history recording is suppressed in connected mode |
| Transform handles / exact axis input | Supported | `ClickDragTool` waits for a full-set reservation; `MovementManager` submits resolved poses; Escape/pointer release cancels waiting grants |
| Move / G → H / Connect | Supported | `MoveToolManager` reserves the frozen set; `Placement` submits poses and target versions; `ConnectTool` submits a reserved translation with source/target preconditions |
| Paint / Default reset | Supported | `ColorSelectionResponse` → reserved `SetParts`; remote apply paints body slots and restores generated defaults |
| Existing-part parameter replacement | Supported | `AddPropertiesUI` → reserved `SetParts`; regeneration preserves identity, paint, state and valid selection, with collider/detector refresh |
| Alt copies / P stamping | Supported for catalog parts | G/R and handles latch Duplicate before waiting; `TransformAndCopy` commits movement and new IDs atomically; `SelectionPlacementData` stamps frozen copies through `AddParts` |
| Paste / build import | Supported for catalog-only builds | `BuildTransferTool` rejects custom geometry/chains before preview; selection follows accepted materialization |
| Selection export / clipboard copy | Existing local read-only paths | ordinary modal guards apply; no shared mutation |
| Custom model import / mounts / depth / grid edits | Disabled; custom documents cannot host | `CustomModelTool` guard and host preflight |
| Chains / simulation playback / Set pose | Disabled; chain documents cannot host | `SimulationTool` guard and host preflight |
| Personal Undo / Redo | Supported for this room membership, in memory | `StateSystem` → `Undo`/`Redo`; authority-owned before/after history, monotonic resurrection, lease conflict checks and invalidation after another participant touches an affected entity |
| Legacy shaft-bracket reposition controller | Disabled | `NewMoveShaftTool.SetShaftPosition` directs users to shared Move |
| Owner document replacement / room admin / revocation | Unavailable | no public endpoint is exposed for these mutations |

Client mutations are `AddParts`, `SetPoses`, `SetParts`, `TransformAndCopy`, `DeleteParts`, `Undo` and `Redo`. Only the authority can create `RestoreParts` inverse events; clients cannot supply inverse content. Unknown mutations fail. The authority does not accept a client author ID, run Unity physics or infer snapping. Creates require fresh stable IDs; pose writes preserve identity/catalog/paint/state; every ordinary existing write needs the owning live lease and expected entity version. History checks reservations atomically and refuses to overwrite another participant’s edits. History survives explicit reconnect with the same identity, but not leaving or restarting the ephemeral room. Preview geometry has no SavedObject, collider, detector or save ownership. Main scene wiring is installed at runtime rather than serialized into the production scene.
