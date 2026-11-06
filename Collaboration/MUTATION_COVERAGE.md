# Connected-mode mutation coverage

The development switch is required. Only the rows marked supported may mutate a connected document. The offline editor retains its existing paths. This audit is a release gate, not a claim of complete collaboration coverage.

| Mutation / action | Connected alpha | Real entry point / guard |
| --- | --- | --- |
| Host current committed catalog build | Supported; no chains/custom models | `CollaborationSession.HostAsync`, `CollaborationScene.CaptureDocument` validate/normalize identities before publishing |
| Join / reconnect / remote apply | Supported | shared client snapshot/cursor + `CollaborationSession` main-thread queue; `CollaborationScene` applies stable IDs, poses, materials and detector refresh |
| Catalog placement | Supported on the free grid, one accepted create per stamp | `Placement.Place` → `PlaceCatalog` → `IEditSession`; specialized hole/hardware/gear targeting is skipped pending read-dependency integration |
| G / R on a placed selection/assembly | Supported | `KeyboardTransformTool.BeginSelection` acquires full-set lease; `CompleteConfirmation` submits exact resolved poses; cancellation restores baseline/releases lease |
| Delete selected parts / assembly | Supported | `ObjectDeletor` → full-set reservation + atomic `DeleteParts`; tombstones prevent identity reuse |
| Save / Save As | Supported independently | `BuildsManager` waits pending barrier, captures accepted snapshot/hash and keeps each participant's path/marker |
| Open / New / leave / continue offline | Supported | normal unsaved handling, detach, accepted baseline preservation; stale async results cannot act on a new session |
| Selection / camera / grid / hole visibility | Local only | ordinary inputs remain available; selection history recording is suppressed in connected mode |
| Transform handles / Move / G → H / Connect | Disabled | `ToolToggle`, `MovementManager`, `MoveToolManager`, `ConnectTool`, and input guards |
| Paint / Default reset | Disabled; existing host paint is preserved | `ColorSelectionResponse` guard |
| Existing-part parameter replacement | Disabled; new placement parameters remain available | `AddPropertiesUI.ApplyParameters` guard |
| Alt copies / P stamping / paste / build import | Disabled | duplicate hold guard, `Placement.StartPlacing`, `BuildTransferTool` guards |
| Selection export / clipboard copy | Existing local read-only paths | ordinary modal guards apply; no shared mutation |
| Custom model import / mounts / depth / grid edits | Disabled; custom documents cannot host | `CustomModelTool` guard and host preflight |
| Chains / simulation playback / Set pose | Disabled; chain documents cannot host | `SimulationTool` guard and host preflight |
| Legacy Undo / Redo | Disabled | input and `StateSystem` guards; personal authority-validated inverses remain future work |
| Owner document replacement / room admin / revocation | Unavailable | no public endpoint is exposed for these mutations |

Wire commands are limited to `AddParts`, `SetPoses` and `DeleteParts`; unknown mutations fail. The authority does not accept a client author ID, run Unity physics or infer snapping. Creates require fresh stable IDs; pose writes preserve identity/catalog/paint/state; every existing write needs the owning live lease and expected entity version. Preview geometry has no SavedObject, collider, detector or save ownership. Main scene wiring is installed at runtime rather than serialized into the production scene.
