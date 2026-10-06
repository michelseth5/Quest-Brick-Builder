# Quest 3 mixed-reality brick builder

Unity 6 (6000.6.x, URP) app for Meta Quest 3: build with real-scale virtual bricks in passthrough.
The owner is new to Unity, so give click-by-click editor instructions when Unity steps are needed.

## Stack
- Meta XR All-in-One SDK (v207): Camera Rig, Passthrough, Comprehensive Interaction Rig building blocks.
  Thumbstick locomotion is disabled (FirstPersonLocomotor component unchecked on PlayerController).
- MR Utility Kit (MRUK prefab in scene) for real tables/floor. Scene permission requested on startup.
- LDraw parts library in `LDraw/ldraw/` (gitignored, ~146 MB). Credit required: see CREDITS.md.
- Rebrickable planned for set inventories / instructions (LDraw OMR files for step data).

## Code (Assets/Scripts)
- `Brick.cs`: shape (imported `BrickPartData` or simple procedural size), stud-to-socket snapping,
  group carrying, overlap checks, table placement, ghost preview. Pivot = part origin; scale must be 1.
- `BrickGrabInput.cs`: controller trigger / hand pinch = one brick; grip / palm = whole connected group.
  Also lets the trigger select GrabInteractors at startup.
- `BrickTray.cs` + `PlaceInFrontOfUser.cs`: older test helper that lays out one of each catalog part.
- `RoomSurfaces.cs`: upward-facing MRUK plane lookup.
- `Sandbox.cs`: sandbox mode. Y = parts menu (categories -> grid of real, grabbable preview bricks),
  B = color menu (`BrickColors.cs`, 30 LDraw colors), A = duplicate held brick/group.
  Spawns baseplate (part 3811) and `TrashCan`. Menus are built in code (`MenuUI.cs`: `MenuPanel`,
  `MenuButton` = RayInteractable + ColliderSurface created at runtime). Grabbable non-brick objects
  (menu handles, trash can) are made by cloning the brick prefab and removing its `Brick` component.
- `MeshBuilder.cs`: procedural box/cylinder meshes.
- `ModelFile.cs`: saves/loads models as LDraw .ldr in `Application.persistentDataPath/Models`
  (positions relative to the model's bottom center, facing the first brick's yaw).
  `ModelMenu.cs`: saved-model grid with spinning thumbnails, Load / Rename (system keyboard) / Delete.
  "Save Everything" in the parts menu; X saves the held model.
- `WristInventory.cs`: Wrench-style forearm slots on OVRCameraRig hand anchors. Real bricks are stored
  shrunk (root scaled, group members kept as children, colliders off); identical items stack by a
  signature string with an "xN" label. Grabbing restores full size (ISDK's GrabFreeTransformer locks
  scale to the size at Grabbable.Start, so loaded items get two frames at full size before storing).
  Saved in .ldr files as `0 !QUESTBRICK WRIST L|R <count>` ... `0 !QUESTBRICK END` comment blocks.
- Parts keep LDraw's built-in colors: one submesh per color (`BrickPartData.submeshColors`, 16 = brick
  color), colored with per-material-index property blocks (`BrickPartData.ApplyColors`). All 322 LDraw
  colors live in `Assets/Resources/LDrawColors.asset` (`LDrawColors.Get(code)`), written by the importer.
- `Sets/` holds OMR set files (.mpd) plus `titles.txt` (display names). "Import Set Parts" imports
  every part a set uses and writes `Assets/Resources/Sets/<set>.asset` (`SetData`: steps of placements;
  submodels are inlined where used).
- `SetBuilder.cs`: Build a Set mode. Ghosts per step on a movable build root, a tray of the step's parts,
  release within 2.5 cm of a matching ghost (same part + color, any angle) locks the part in place
  (`Brick.SetLocked`); steps auto-advance. The parts tray has its own handle. Finished builds are
  bonded (`Brick.BondTogether`): grip takes the whole bond, trigger pulls one brick out of it.
  Bonds are saved as `0 !QUESTBRICK BOND <n>` before each part line and kept for duplicates/wrist items. Start menu (Free Build / Build a Set) opens at launch and on
  the left controller's menu button (`OVRInput.RawButton.Start`).
- `Assets/Editor/LDraw/SetFinderWindow.cs` (Tools > Set Finder): Rebrickable search (key in
  gitignored `Secrets/rebrickable.key`, never print it), OMR availability check, "Add" = Rebrickable
  parts list (LDraw ids/colors via external_ids, spares excluded, minifig parts included) + OMR steps
  if available + part import + `SetData` + CREDITS.md line.
- `PartsBin.cs`: Build It Yourself mode. A panel with one set's parts (`SetData.PartsList()`), category
  filter + pages, counts; grabbing a preview takes one; trashing a set part returns it (`Brick.Trashed`);
  Finish Model bonds the built parts. Only on-screen previews exist as bricks. Grip-grabbing a bin
  preview takes the whole remaining stack ("xN" label); stored at a wrist it becomes an xN slot
  (`WristInventory.Stored` + `AddLoaded`), released elsewhere the extras return to the bin.
- `InstructionBook.cs` + `Assets/Plugins/Android/PdfBridge.java`: PDFs picked via ACTION_OPEN_DOCUMENT
  (headless android.app.Fragment) are copied to `persistentDataPath/Instructions/<set> booklet <n>.pdf`;
  pages rendered by PdfRenderer on a thread to `Instructions/cache/<booklet>/<page>.jpg`, results via
  UnitySendMessage to the Sandbox GameObject. Portrait pages show as a two-page spread, landscape alone.
  Android-only (app entry is GameActivity). Check Java with javac + android.jar + Unity classes.jar.
- Roadmap: A) Set Finder (done), B) Build It Yourself (written), C) instruction book (PDF pages vary
  between portrait and landscape; the book must adapt and be freely movable/tiltable): LEGO PDFs the player
  downloads in the Quest browser, imported via the Android file picker (Storage Access Framework,
  needs a small Java plugin in Assets/Plugins/Android) into app storage, rendered with Android's
  PdfRenderer, shown as a two-page floating book. Do NOT scrape lego.com/Rebrickable for PDFs
  (bot protection + terms); never commit PDFs.
- `Assets/Editor/LDraw/`: `LDrawParser.cs` (pure C#, System.Numerics, testable with `dotnet`) and
  Tools > LDraw Part Importer window, which writes meshes + `BrickPartData` to `Assets/LDrawParts/`.

## Connections (non-stud snapping)
- `LDraw/shadow/` = git clone of github.com/RolandMelkert/LDCadShadowLibrary (CC BY-SA 4.0, gitignored).
  `Connections/` (committed) adds files in the same format for parts it misses (e.g. 6015 tyre, 6014a rim).
- `LDrawParser` collects `!LDCAD SNAP_CYL/CLP/FGR/GEN/INCL/CLEAR` metas through sub-files (CLEAR drops
  inherited ones) into `ConnectorData` (Unity space; shapes extend along the meta's -Y). Duplicates removed.
- `Connector.cs`: runtime `Connector` + `Connections.Fit/QuarterTurns/AreMated/Align`. Pegs fit holes of
  equal radius (R in R/S, A in R); clips take male R of their radius; fingers/generic match same profile,
  opposite gender. Stud-like (R6/S6) pairs and axle-in-cross-hole (both have an A token) pairs snap to
  quarter turns; others spin freely. Non-sliding pegs enter a hole through the end they point toward
  (same direction: peg base at the hole's start; opposite: at the hole's far end). Sliding cylinder pairs
  (Technic pins/axles/holes/bars) click to half-stud steps from the fixed shape's start, or rest flush
  against a part already on the same shaft when within 3 mm (`SlideShift`, anchors from `PartnerEnds`).
  A denser plate+half-stud grid from every anchor was tried and made placement inconsistent; avoid it.
  Clips slide freely. Single-connection snaps prefer centering the part over the connector. A connector counts as taken only where an existing
  partner occupies the same span along it (`Connections.SpanAlong`), so axles/bars/pins hold several parts.
  `Brick.FindSnap`: aligns each candidate pair, keeps the 6 closest, then also tries rolls about the
  connection that line up a second connector, scoring by distance/turn minus 2 mm per extra connection
  plugged in (fixes studs on free-spinning pins, e.g. a 3x3 round tile on 4 half-pins in a gear). Snaps are rejected
  only if a connector they'd use is already mated with a third part (`UsesTakenConnection`); box overlap
  checks were dropped for snapping because part bounding boxes are wrong for clips/brackets/car bases.
  Parts with upward R6 studs but no upward R6/S6 sockets anywhere get the estimated footprint grid added at import
  (`AddMissingBottomSockets`; a centred "jumper" stud doesn't shift that grid). Bars (R4) fit axle holes (A6). Brick snapping, group carrying, and group free-connector lists all use
  connectors; parts without data fall back to `studs`/`antiStuds`.

## Undo (`UndoHistory.cs`)
- Each grab records a step at Select: trigger = the brick's pose + bond; grip = the brick plus carried bricks
  relative to their original parents; posing = the bricks the joint turns; menu/bin previews and A-duplicates
  = spawn steps (undo deletes them, firing `Trashed` so the bin takes them back). Trashed bricks are
  deactivated, not destroyed, until their step falls out of the 30-step history. Wrist storing and set
  placement discard the step. Undo = left thumbstick click or the parts menu's Undo button (not while holding).

## Posing joints (`Articulation.cs`)
- Links between bricks are rigid if any mated pair is quarter-turn/generic or two pairs lie on different
  lines; otherwise a joint around the shared axis. A grip grab calls `Articulation.TryBegin`: two-handed
  only (the grabbed brick is carried by another held brick); turns the grabbed side away from the holder.
  Re-applied on release and once more the next frame so the part doesn't come loose. Hand position from
  the Grabbable's Move events; applied in `Brick.LateUpdate` after the grab system. Optional click angles
  in Settings (`GameSettings.JointStep`). Bonds are ignored when splitting a model at joints.

## Notes
- LDraw -> Unity: (x, -y, z) * 0.0004 m; front-facing LDraw polygons are reversed for Unity winding.
- Underside sockets are inferred as a stud grid over the part's footprint (LDraw doesn't mark them).
- Scripts can be test-compiled outside Unity with a netstandard2.1 csproj referencing
  `<Unity>/Editor/Data/Managed/UnityEngine/*.dll` and `Library/ScriptAssemblies/*.dll`.
