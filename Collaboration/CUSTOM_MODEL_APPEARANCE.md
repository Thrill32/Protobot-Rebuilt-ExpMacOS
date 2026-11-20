# Imported model appearance

Import an OBJ from a directory that also contains its MTL files and diffuse images. The importer supports RGB `Kd` channels in the range 0–1, one `map_Kd` PNG or JPEG per material, and explicit OBJ texture coordinates, including relative indices and UV seams. A texture modulates its material's diffuse color. A texture with no `Kd` uses white, and faces without a diffuse color or texture use the standard custom-model material. OBJ and STL geometry-only imports use the existing single-material path.

Use one relative MTL path per `mtllib` line. Resolve image paths relative to the declaring MTL. Use forward slashes for subdirectories. The importer checks paths against the selected directory tree and rejects absolute paths, parent segments, remote URLs, symlinks and junctions. Referenced libraries, used materials and used image files must be present. Invalid dependencies and UVs report an import failure before placement. The 64 MiB source model, 500,000-triangle and 1,500,000-vertex limits still apply, including vertices split at UV seams.

GLB 2.0 import supports one self-contained static scene with indexed or unindexed triangles, node transforms, opaque base colors, and embedded PNG/JPEG base-color maps. It reads the GLB default scene and converts its meter units to inches. Diffuse textures use repeating UVs and the built-in bilinear filter. Export uncompressed geometry and bake any texture transforms into the UVs before importing. External buffers and images, required extensions, skinning, animation, vertex colors, morph targets, transparency, sparse or compressed mesh data, and clamp or mirror wrapping are outside this subset. Secondary PBR maps, filtering preferences, and authored normals are omitted. Imported GLB geometry and appearance use the existing save and collaboration formats. [Khronos glTF 2.0 specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html)

The imported diffuse material keeps color in range under scene lighting. For `Notes/KirbyObjMTL1`, both maps contain the intended pink (RGB 245, 152, 176). The previous lit material clipped broad areas of the body toward white. The matte material keeps directional shading and preserves pink in both the supplied OBJ and its Blender-exported GLB counterpart.

Paint applies one solid RGB color to all the body's material slots and removes their diffuse maps from rendering. Default restores each original color and map. The immutable imported data stays on the model asset throughout paint, copies, mounting-point edits and history. Each instance has its own Paint setting and mounting points. Saves and transferable selections embed the appearance, so moved or deleted source files have no effect.

## Bounds

| Resource | Limit |
| --- | --- |
| Referenced MTL files | 8, each up to 256 KiB |
| Material definitions and used material slots | 32 each, including any unassigned slot in the used count |
| Diffuse textures | 8 distinct local paths across used materials |
| Compressed source image bytes | 16 MiB total |
| Diffuse image dimensions and decoded pixels | 2048 × 2048 maximum, 16 MiB total RGB24 |
| Encoded asset and retained room | 64 MiB each, appearance included |
| Shared room accounting | 128 MiB retained decoded cost and 128 MiB live instance cost, appearance included |
| Unity appearance-bearing models | 384 MiB logical resource cost across live models, copies, templates and retained undo objects |

Texture dimensions, decoded RGB size and projected encoded asset size are checked before Unity decodes pixels. The importer normalizes images to opaque RGB24 and encodes the pixels in base64url. This costs four encoded bytes per pixel and gives receivers a dimension-checked pixel buffer without invoking an image codec. The accounting also charges retained UTF-16 pixel strings, four bytes per pixel for native texture backing, mesh and UV/triangle data, and 4 KiB per material slot. These are quota estimates. Transient parsing, native decoding, graphics driver and other application memory are separate. Two instances can reference the same stored asset, and each pays its own live resource cost.

Measurements from the focused lab:

| Fixture | Encoded asset | Other cost |
| --- | ---: | ---: |
| Two triangles, plain | 172 B | |
| Same geometry, two diffuse colors | 362 B | 190 B added |
| Four triangles, two materials and a 64 × 64 PNG map | 16,845 B | 16,384 B pixel text |
| Four triangles, two materials and a 64 × 64 JPEG map | 16,847 B | 16,384 B pixel text |
| One triangle, 2048 × 2048 diffuse map | 16,777,551 B | 12,582,912 B raw RGB and 50,335,976 B accounted resource cost |
| Existing 16,300,084-byte precision STL | 43,578,311 B | Same encoding size as before |

Each 1024 × 1024 map contributes exactly 4 MiB of pixel text before geometry and material metadata. Separate models with the same name, geometry and embedded appearance share one content hash. Color, triangle assignment, UV and image pixel changes participate in hashing. Texture references to a common path share one pixel buffer within the asset. Renderer resources are released with their instances, when offline redo history is discarded, and when switching builds. Replacement releases old room assets and runtime resources. Assets needed by accepted history remain available for undo. Unreferenced successful uploads are subject to the existing 16-asset and 64 MiB unused quota.

## Compatibility

Updated helpers advertise and require appearance version 1 at admission. Update the player and helper together to share appearance. Updated players can still join previous compatible helpers for geometry-only work, including the 8 MiB fallback. They reject appearance upload, remote room creation and remote room replacement before sending appearance to an unsupported helper. The plain asset wire shape and hash, ordinary document version and existing metadata limits are unchanged.

A first appearance asset writes SQLite version 4 atomically with the asset. Appearance-bearing backups use archive version 4 and portable room exports use schema 2. An older helper refuses the versioned store/export. The new helper still reads older plain storage and backups. Replacing a room with a plain project writes the existing version 3 after the transaction retires previous content. Keep a stopped pre-upgrade backup if rollback is needed.

Appearance-bearing local `.pbb` files carry an appearance format header and appearance-bearing clipboard selections use the version-2 prefix. Previous local builds and clipboard selections remain readable. Older readers refuse the new formats. For existing collaboration rooms, preserve the stored catalog and use the player's established `--collab-catalog` override if needed.

Diffuse import covers opaque surface color only. Unsupported inputs include OBJ vertex colors and authored normals, transparency, material-map options, normal/roughness/metallic maps, PNG iCCP, iTXt and zTXt chunks, animated PNG, archives, and remote textures. PNG and 8-bit baseline/progressive JPEG are supported. Texture filtering uses a repeating, non-mipmapped diffuse map.

## Validation

Run the focused .NET lab with the pinned SDK and `--appearance-only`. It covers parser and path rules, content hashing and deduplication, compatibility, byte and live quotas, late join, paint history, restart, replacement and backup/restore. Set `PROTOBOT_TEST_APPEARANCE_LEGACY` to a retained pre-appearance helper for its actual v4 refusal. Run `--large-model-only` to check the prior 64 MiB workflow.

Use a new output directory for the rendered Mac check:

```sh
python3 Collaboration/tools/check-custom-model-players.py --appearance \
  --player Collaboration/artifacts/custom-model-appearance/players/Protobot.app \
  --output /private/tmp/protobot-appearance-players
```

The check uses a host-owned durable helper and separate player profiles. It checks offline creation, Undo/Redo and release of an abandoned redo branch before hosting. It imports a colored PNG model as owner, joins a second player after that model is accepted, and imports a colored JPEG model as editor. It checks renderer materials and UVs, copies, a mounting edit, Paint/Default/Undo/Redo, clipboard and local saves, dependency and dimension failures, source-free reload, both clients' state after helper restart, and an editor replacing the room from a local save followed by owner resynchronization. The OS file picker alone is substituted during import. Windows/Linux/Pi execution and native picker focus are separate checks.

### GLB and Kirby checks

Run the .NET lab with `--glb-only` to check the static scene, embedded image, transform, meter, winding, UV, material and rejection paths. Run Unity with `-batchmode -quit -executeMethod ProtobotEditorTools.CustomModelAppearanceValidation.InspectFixture -customModelFixture <file> -customModelCapture <directory>` to inspect and render a local OBJ or GLB. Use `Notes/KirbyObjMTL1/scene.obj` to check the original material and body textures. Omit `-nographics` when capturing renders. Pass one or more `-customModelFixture` paths to `ProtobotEditorTools.CustomModelValidation.Smoke` for runtime import, clipboard persistence, and Paint/Default checks. The original Kirby OBJ and a Blender-produced GLB both passed in Unity. The GLB lab passed 17 assertions, alongside 29 appearance and 14 large-model assertions.

## Built artifacts and test results

The matching Mac arm64 and Windows x64 development players and the macOS arm64, Windows x64, Linux x64 and Linux arm64 helpers are in [the local artifact directory](artifacts/custom-model-appearance/). The players were built with Unity 6000.6.9f1, the editor used for the prior 64 MiB artifacts. The repository's checked-in editor settings were retained. The artifact directory also includes the pre-appearance catalog for the existing `--collab-catalog` override.

The appearance lab passed 30 assertions, the 64 MiB regression passed 14, and replacement storage passed 49. The existing Unity geometry-only parser and runtime smoke both passed. The Mac pair rendered the two textures through restart and completed a room replacement at revision 10. The final headless pair also verified that abandoning offline redo releases its appearance resources. A final graphical rerun stalled in Metal's presentation wait before importing, so the retained screenshots come from the preceding rendered pass. The Windows player and Linux helpers were built and checked as artifacts. Runtime execution was on macOS.
