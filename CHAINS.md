# Sprocket chains

Chains stay physically planar. The whole plane can have any world orientation, and individual sprockets can face either way. Skew shafts, offset planes, twisted chains and crossed belts are outside this routing model. Normal and high-strength chain sizes cannot be mixed. Uniform positive scaling retains the existing visual scaling behavior.

## Routing controls

Select the sprockets and press **C** (the rebindable **Chain Selected Sprockets** action) for a quick outer loop. Selection order has no effect. Press the action again on the same endpoint set to remove the chain, including a chain whose endpoints have since moved into an invalid layout.

For an inner idler or an explicit route, hold **Shift** while invoking that same action: **Shift+C** by default. The animation panel also has a **Chain route…** button. If the rebound Chain chord already includes Shift, it retains quick-chain behavior; use that button to edit the route. The editor numbers the sprockets in the viewport and lists those same numbers. Use **Up/Down** to define the cyclic visit order and **Outer/Inner** to choose each wrap. The last entry connects back to the first. **Reverse order** reverses traversal while retaining each sprocket's wrap choice and physical route. Starting suggestions are deterministic; the original selection order is never the route contract.

An outer wrap encloses its sprocket on the main loop; an inner wrap bends the chain the other way around an idler. Inner wraps reverse the sprocket's world rotation relative to outer wraps. This does not mean that two sprockets may use a crossed two-wheel belt: a planar chain must form one simple closed contour. Some orders and wrap combinations are physically impossible. Their preview is hidden, the reason is displayed, and **Apply route** stays disabled. Move sprockets first when the chosen route has no clearance.

The cyan preview and numbered markers are private. They do not transmit motion or enter saves, copies, collaboration presence or undo history. **Apply route** creates one undoable edit, retaining an existing chain's identity. **Cancel**, **Escape**, focus loss, loading or a shared revision discard the preview. The editor captures scene editing while camera navigation remains available. **Remove chain** removes the existing connection as one undoable edit.

## Geometry and motion

`ChainRouteGeometry` constructs signed common tangents between circles. Inner and outer choices determine tangent branches and arc curvature together, rather than choosing each segment independently. The winding follows the ordered planar polygon, so reversing the order or flipping a sprocket face cannot introduce long unintended wraps. Validation rejects duplicate/missing members, incompatible pitches, skew/offset planes beyond the existing alignment tolerances, nonuniform/nonpositive scales, overlapping sprockets, degenerate wraps, blocked runs, crossing runs, insufficient separated-run clearance, excessive contour size and incorrect total winding.

Rendering uses measured tooth-tip radii plus ribbon half-width and maximum arc-chord sagitta. Straight runs are subdivided at wrap resolution to keep Unity's ribbon from folding at arc joins. Mechanical transmission continues using tooth counts and chain pitch, independently of those visual radii. The same contour supplies wrap signs to the simulation graph and texture travel. Baseline evaluation remains reversible and animation previews keep the normal save/commit contract.

A missing, deleted or temporarily incompatible endpoint hides the contour and disables transmission. Restoring the endpoint/valid pose restores the existing logical chain. Incomplete or invalid local contours are omitted from ordinary scene snapshots. Complete endpoint copies retain cyclic order and wrap choices while receiving fresh endpoint and chain identities. This includes offline Alt/axis copies and frozen P/clipboard/build imports; canceling temporary copies removes their chains. Undoing endpoint duplication/deletion hides incident chains until redo/restoration.

## Persistence and collaboration

`ChainData` retains `members` and adds optional binary fields `id`, `routeVersion` and `wraps`:

- **Version 0** (missing/default): `members` is the legacy unordered endpoint set; `wraps` is absent or empty. Its outer hull is computed deterministically. Existing binary and JSON builds need no migration.
- **Version 1**: `members` is a cyclic route, and `wraps` has exactly one integer per member: **0 outer**, **1 inner**. No duplicate members, unknown versions, omitted entries or other wrap values are accepted.

Saving an existing legacy chain does not silently change its routing mode. Editing and applying it writes version 1. Build dirty comparison includes route adjacency and per-member wraps, with cyclic rotations/reversal treated as equivalent. New saves retain chain identity; copies remap endpoint IDs and allocate a fresh chain ID. Unknown or malformed explicit route data is rejected before transfer/materialization, and file loading preflights explicit geometry before replacing the current build. Legacy malformed optional records keep the previous skip behavior.

Collaboration protocol **0.7.0-alpha** carries the same route metadata in `ChainRecord`. Endpoint sets still identify duplicate connections. Explicit routes are canonicalized only by cyclic rotation/reversal with wraps attached to their members; they are never sorted as an unordered set. Hashes and personal history include route data; legacy route hashes remain byte-compatible for durable recovery. Old 0.3–0.6 rooms can be recovered, but helpers and participating clients must be rebuilt together.

Apply/remove uses `SetChains`, retaining the existing chain ID, entity versions, tombstones, endpoint leases and expected versions. Copies, accepted saves/exports, deletion and undo/redo retain the route. Remote clients materialize from accepted order/wrap metadata deterministically. The authority validates structure and existence; Unity clients validate physical geometry. Structurally valid accepted entities remain logical entities when a later endpoint transform temporarily invalidates their contour, including across remote rematerialization. Such a contour stays hidden and cannot transmit motion.

## Validation

See [VALIDATION.md](VALIDATION.md) for Unity commands. `ChainContourValidation.Validate` checks numerical contours, full ribbon coverage from both sides, inner curvature (including adjacent inner sprockets), finite/sample-count bounds, reversed order, flipped faces, oblique planes, mechanical signs, texture travel, binary/JSON compatibility (including binary fields omitted by an old writer), canonical remote materialization and malformed geometry/metadata. `ChainContourValidation.Render` adds front/back inner-idler images to the existing unequal-size probes.

`ChainRoutingValidation.Smoke` exercises the actual Shift+C/controller and UI Apply path, preview/save isolation, reversal, cancellation, route creation/edit undo/redo and stable identity, direct/P/clipboard copies, endpoint deletion/restoration and binary/JSON scene loads. `-chainRoutingRenderProbe` captures the editor. Existing mechanism, simulation, transfer and collaboration smoke suites cover shared paths. The collaboration lab checks canonical routes, malformed route rejection, durable history and both network transports.
