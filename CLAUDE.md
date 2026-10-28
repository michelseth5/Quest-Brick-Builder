# Quest 3 mixed-reality brick builder

Unity 6 (6000.6.x, URP) app for Meta Quest 3: build with real-scale virtual bricks in passthrough.
The owner is new to Unity, so give click-by-click editor instructions when Unity steps are needed.

## Stack
- Meta XR All-in-One SDK (v207): Camera Rig, Passthrough, Comprehensive Interaction Rig building blocks.
  Thumbstick locomotion is disabled (FirstPersonLocomotor component unchecked on PlayerController).
- MR Utility Kit (MRUK prefab in scene) for real tables/floor. Scene permission requested on startup.
- LDraw parts library in `LDraw/ldraw/` (gitignored, ~146 MB), plus the unofficial library in `LDraw/unofficial/`
  (gitignored; `LDrawParser.FindPartFile` / `Load()` search official parts, then unofficial). Credit required: see CREDITS.md.
- Rebrickable planned for set inventories / instructions (LDraw OMR files for step data).

## Code (Assets/Scripts)
- `Brick.cs`: shape (imported `BrickPartData` or simple procedural size), stud-to-socket snapping,
  group carrying, overlap checks, table placement, ghost preview. Pivot = part origin; scale must be 1.
- `BrickGrabInput.cs`: controller trigger / hand pinch = one brick; grip / palm = whole connected group.
  Each thumbstick push quarter-turns what that hand holds (`Sandbox.TurnHeldWithThumbsticks` -> `Brick.TurnInHand`:
  left/right about up, up/down about the level line across the view; re-takes the GrabFreeTransformer's offset).
  Also lets the trigger select GrabInteractors at startup.
- `BrickTray.cs` + `PlaceInFrontOfUser.cs`: older test helper that lays out one of each catalog part.
- `RoomSurfaces.cs`: upward-facing MRUK plane lookup (tables, floor, ...); falls back to the floor-level tracking
  space height when the room scan has nothing there. Bricks only set down within `maxSurfaceSnap` (3 cm), else float.
- `LockLight.cs`: a small light on each controller while that hand holds something: red = all locked,
  green = none, amber = locked model with loose pieces (offset from the hand anchor is a guess; adjust `Offset`).
- `Sandbox.cs`: sandbox mode. Y = parts menu (categories -> grid of real, grabbable preview bricks),
  B = color menu (`BrickColors.cs`, 30 LDraw colors), A = duplicate held brick/group.
  Spawns baseplate (part 3811) and `TrashCan`. Menus are built in code (`MenuUI.cs`: `MenuPanel`,
  `MenuButton` = RayInteractable + ColliderSurface created at runtime). One-line texts keep a readable size (shrink to
  80% of the box height at most) and end in "…"; a button's `MarqueeText` scrolls the rest while it's pointed at.
  Plain texts can scroll too (`MarqueeText.AddTo`: invisible RayInteractable). Bin labels are quantity first
  ("3x Brick 1 x 2") and scroll; the bin has Type and Color filter toggles (PlayerPrefs `BinFilterType`/`BinFilterColor`),
  each a column when on, combined (a column lists only groups with parts passing the other filter; neither = all;
  colors ordered by part count, with a swatch chip). Grabbable non-brick objects
  (menu handles, trash can) are made by cloning the brick prefab and removing its `Brick` component.
- Fix Snapping (Y menu, hold the piece; `SnapEditor.cs`): the part enlarged (longest side 20 cm) and see-through on a
  grabbable stand (`Sandbox.CreateGrabbableProp` with an invisible box; the prefab has no laser grab, so markers inside
  stay clickable),
  stand, each connector a cylinder marker (`MeshBuilder.UnitCylinder`; male green, female blue, clip orange, finger
  yellow, generic/ball purple), clickable via `Pointable` (MenuUI.cs: RayInteractable + ColliderSurface on any
  collider). Panel: kinds down the left (`Families`: Studs, Bars & Clips, Axles, Pins, Hinges, Special, with counts);
  picking one lights it, pulses its markers (others dimmed) and shows its "+" buttons + extras on the right (hinge
  kinds; Longer/Shorter). X/Y/Z axis lines + facing labels through the part origin (`BuildAxes`).
  Select -> nudge X/Y/Z by the grid (1/2/5/10 LDU), Point Another Way (6 axes), Longer/Shorter (2 LDU,
  sections redone), Delete. "+ Type" then click the piece (its MeshCollider is on only while adding, so markers inside
  stay clickable): hit point to the grid across the surface, 0.5 LDU along the normal; male points out, female in,
  clips across; shapes copied from shadow-library parts (stud R6 4, socket R6 4, bar R4 16 slide, bar hole R4 8,
  clip R4 8 centered, axle/axle hole A6 20, pin R6 20, pin hole "R8 R6" 20 with R8:2 collars). Save ->
  `ConnectionOverrides` (`persistentDataPath/Connections/<part>.json`, the whole Connector[]; applied when parts are
  looked up: Sandbox catalog + `PartStore.Find`; `Brick.ConnectionsChanged` rebuilds every brick of the part).
  Restore Original deletes the file. In the editor's Play mode, Apply/Save change the part assets themselves.
  "+ Hinge" adds a Finger connector across the surface, 6 LDU out, of the kind picked from `HingeKinds` (copied from
  44301/44302 (locking, lckHng F6), 4275/4276 (classic F4), 3938/3937 (hinge brick, hgBrC)); picking a kind also
  changes the selected hinge. Marker cylinders have both caps. All menu text is 0.83x (`MenuStyle.TextScale`).
  Next rounds: suggestions from the part's primitives (round/axle holes), test-fit partner ghosts.
- `MeshBuilder.cs`: procedural box/cylinder meshes.
- `ModelFile.cs`: saves/loads models as LDraw .ldr in `Application.persistentDataPath/Models`
  (positions relative to the model's bottom center, facing the first brick's yaw).
  `ModelMenu.cs`: saved-model grid with spinning thumbnails, Load / Rename (system keyboard) / Delete. Each thumbnail is
  ONE mesh (`ModelThumbnail`: biggest pieces first, studs/tubes left out, 100k-triangle budget) saved beside the model
  as `<model>.thumb` at save time (Rename touches it, Delete deletes it; older saves get one on first view); shown one
  per frame after opening and cached in memory by
  file write time (one object per piece made opening the menu stutter 5-15 s with big saves).
  "Save Everything" in the parts menu (X no longer saves; it toggles locks, below). After a model is loaded (or saved),
  Save Everything asks: Replace "<title>" (`ModelFile.Save(..., replacePath)`, same title) / Keep It and Save a New
  Model / Cancel (`Sandbox._workingSave`, `SaveOrAskToReplace`; bricks gathered when the button is clicked).
  Loading runs as a coroutine (`Sandbox.LoadingModel`, ~12 ms of work per frame) behind a `ProgressDisplay` bar: parts
  first, then locked groups via `LockedBody.BuildSteps` (an iterator yielding 0-1 per stage/body; `Build` drains it).
  `MarqueeText` leaves text alone if something else changes it mid-scroll (e.g. "Click again to ...").
  Each load appends stage times (wall time) + `LockedBody.LastTimes` (work per build stage) + the next 5 s of frames
  to `persistentDataPath/load-times.txt` (`Sandbox.LogLoad`); pull it with adb to see where slow loads go.
  BuildSteps yields every 32 pieces and every combined chunk, so the bar keeps moving within one big body.
  First log (Oct 22, finished Cobra, 1,210 locked pieces, 88.6 s): building bodies 0.23 s; 88 s was the bin being
  redrawn once per resumed piece (+ a 17 s frame destroying the previews). `PartsBin.TakePieces` now redraws once.
- Drift check: each lock runs `LockedBody.FindDrift` while building bodies: studs more than 0.5 LDU short of fully
  in, and studs/Technic pins not in anything but within 2 mm of a hole they fit (`NearlyIn`). Results in
  `LockedBody.Drift` (room positions); `Sandbox` adds "Heads up: N spots aren't fully plugged in" to the lock message
  and shows red markers there for 10 s (Settings > "Mark pieces not fully plugged in", `GameSettings.ShowDriftMarkers`). Offline on the Oct 14 Cobra save: car 56 short + 14 near misses, door 1 + 2.
  X with nothing loose but several locked groups (units) in the model first joins them into one (`JoinGroups`;
  Oct 26 castle save: 3,341 locked pieces in 6 groups, so one-finger grabs left groups behind); the next X unlocks.
  Auto-lock (`Sandbox.AutoLock`, Settings: after 5 s / 15 s / Off, `GameSettings.AutoLockSeconds`): once nothing has
  been held that long, each model (2+ pieces) locks its loose pieces (`LockLoose(..., automatic)`, quiet unless the
  drift check finds something); off during Build a Set; clears undo. Ghost snap searches over 4 ms are spaced out
  (`Brick._nextSearchAt`, ~25% of the time; release still searches fresh). Shadows: the Quest uses the URP package's
  default asset (2,048 px over 50 m, flickering); `Sandbox.ApplyShadows` sets 3 m at startup and Settings > Shadows
  turns lights' shadows off/on.
- Locked sections: `Brick.LockedSection` (0 = loose; numbers count up in the order locked, `Brick.NewestSection`).
  X (`Sandbox.ToggleLock`) on the held model, else the one within 12 cm of the left controller (`Brick.Nearest`,
  `Brick.WholeModel`): locks its loose bricks as a new section, or if none are loose unlocks the newest section.
  A locked brick can only be grabbed with its whole model; models holding locked bricks can't be trashed. Saved as
  `0 !QUESTBRICK LOCK <n>` before the part line; loading offsets by the sections already in the room.
- Assemblies: parts LDraw supplies as "(Complete)"/"Poseable" files that only place other parts (61903 universal
  joint = 62520 x2 + 62519; 73983 hinge plate; 3829c01; 10509) get `BrickPartData.components` at import (the pieces
  are imported too). When such a brick is put down, `Sandbox.SplitAssembly` (via `Brick.AssemblyPlaced`) swaps it for
  its pieces if they hold together by connectors, so their joints pose; the undo step is discarded. The bin counts
  one assembly per full set of pieces (`PartsBin.TakeFromBin`). Hoses/chains are segment assemblies too but not split.
  Posing picks the joint nearest the grabbed side, so a universal joint bends one axis per grab.
- Ball joints: shadow `SNAP_GEN` with `bounding=sph R` and `match=size` or `placement=free` -> `Connector.Kind.Ball`,
  profile "B<R>" (ball M, socket F). Same size + opposite gender fits; snaps centre to centre keeping the held angle
  (`Align` skips the turn); counts as a point (`Connector.IsPoint`) for mating/used checks but is a joint, not rigid:
  `Articulation` swings the moving side around the ball following the hand (`_ball`); a second connection alongside a
  ball makes the link rigid. 64872 (reinforced suspension arm) gets its socket and pivot holes from 57515 by hand.
- Bars: a male, open-ended "R4" cylinder 8+ LDU long is made sliding at import (cutlery handles), so it rests on
  whatever already occupies a hollow stud instead of being refused.
- Locked groups: every body has a `unit` (pieces carry it too; saved as `0 !QUESTBRICK GROUP <n>`, renumbered on
  load). `LockedBody.Build` groups by unit, never by touching, so locked groups snapped together stay separate;
  pieces without a unit (old saves) group by connection. `Sandbox.Collapse` puts newly locked bricks into the
  biggest locked group they touch (or a new one) and rebuilds only those groups. Trigger on a locked body takes
  that body alone (also off the model in the other hand); grip takes the whole connected model. Every rotation
  through a body (Build, Explode, load, save) goes through `LockedBody.Unit` (normalize): Unity's Quaternion.Inverse
  assumes unit length, and without it each lock/unlock cycle doubled the error (simulated: 0.5% at 15 cycles, 17% at
  20; Oct 14 save had pieces 2-16% stretched and pushed apart). Unlocking flashes
  what came loose (`Brick.Flash`): green for a lone piece, yellow for pieces still joined to each other. The flash copy
  is a child of the brick, destroyed in `StopFlash` (also from OnDisable): as a separate follower it was left behind
  when the brick was locked/trashed mid-flash and looked like resized, misaligned pieces.
- Locked bodies (`LockedBody.cs`, Settings > "Locked pieces as one object", `GameSettings.CollapseLocked`): locking
  swaps a model's locked bricks for ONE solid `Brick` (`Brick.BecomeLockedBody`, `IsLockedBody`, `PieceCount`, no
  `part`): `LockedBody.Build` groups pieces by any connection (rigid or joint) plus bonds, so a file with several
  locked models still gives one body each. Joints inside a body don't pose (splitting bodies at joints was tried:
  Technic pins are joints, so a locked car was dozens of bodies and two-handed grabs shifted or tore it; don't go back
  without an explicit per-joint opt-in). A body holds its pieces (local poses, section,
  fromSet), combined meshes per 128 mm cell (children "Locked Pieces"), grab boxes per 32 mm cell (BoxColliders on
  the root, added before ISDK's Start collects colliders), and only its exposed connectors; partly used ones keep
  the connectors covering them (`fullyUsedInside`/`partlyUsedInside` seed `UpdateConnectorUse`). Bricks with 48+
  connectors use a local `ConnectorGrid` (`ForEachMatedPair`, used by `IsConnectedTo`, `UpdateConnectorUse`,
  `Articulation.Classify`). `Sandbox.Collapse`/`Unlock` rebuild every body of the model on each X (full rebuild; fine
  to ~1,000s of pieces, make incremental later). X on a held model is deferred until it's put down
  (`_pendingLock`). Bodies can't be trashed, duplicated, or stored on a wrist; undo history is cleared on lock/unlock.
  Saves write each piece (`Locked.Explode`) with `LOCK <n>`; loading builds bodies straight from placements.
  Bodies over 150k triangles show no ghost.
- Hidden studs: the parser tags every stud-family primitive instance (stud*/stu2*: top studs, tubes, posts) as a
  detail and records its index ranges (`BrickPartData.details`); all of them use LDraw's simplified (p/8) shapes.
  `LockedBody.Stripped` builds per-part mesh variants (cached per build by which details are dropped) without
  studs plugged end to end into an opaque, not-open-through piece, and without underside tubes when every socket on
  that side is plugged that way (`GameSettings.HideCoveredStuds`, read when a body is built). Measured Oct 2026
  (all parts, after simplified tubes): Cobra 561k triangles, 21% studs/tubes; Eiffel 10307 3.78M, 35%, floor 2.45M;
  Barad-dur 1.66M, 52%. So the biggest sets also need distance LOD / culling. Not done yet: LOD.
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
  Save Everything during a bin build writes `0 !QUESTBRICK SET <set> BOOK <booklet> <page>` and `0 !QUESTBRICK FROMSET`
  before bin parts (`WRIST ... FROMSET` for wrist stacks); loading it calls `PartsBin.Resume` (bin minus those parts)
  and `InstructionBook.ResumeAt`. The book also remembers each set's booklet/page in PlayerPrefs (`BookPlace:<set>`).
- Removing sets: "Remove a Set" under both set lists (click twice). Built-in sets can't leave the APK, so
  `SetData.Remove` hides them (PlayerPrefs `RemovedSets`; `LoadAll` skips them; Settings brings them back);
  `InstructionBook.DeleteInstructions` deletes the PDFs, page cache and saved page. Saved models stay.
- Get New Sets (start menu; `SetDownloadMenu.cs` + `Assets/Scripts/LDraw/SetDownloader.cs`): the headset adds sets itself.
  Player's own Rebrickable key in `persistentDataPath/rebrickable.key` (Settings > Rebrickable key; the owner's can be
  adb-pushed from Secrets/, never into the APK). One-time library download (~236 MB) to `persistentDataPath/Library`:
  complete.zip (entries under ldraw/), ldrawunf.zip (top level), shadow.zip (GitHub codeload, LDCadShadowLibrary-main/),
  read unzipped through `ZipLibraryFiles` (`LDrawParser.Files`, an `ILibraryFiles`; disk by default; names case-insensitive)
  plus our Connections/ packed into `Resources/ConnectionsBundle.txt` (`ConnectionsBundler`, before every build).
  Search = Rebrickable sets (7 per page) + OMR HEAD check; adding = parts list (page_size 100: 500 gives 502 for big
  sets; requests retry 5xx), OMR model to `persistentDataPath/Sets/<set>.mpd`, parts parsed on a background thread,
  meshes + `PartStore` files (`persistentDataPath/Parts/<id>.part`, versioned binary) on the main thread, then
  `SetData.SaveDownloaded` (`Sets/<set>.json`; `LoadAll` merges, a download replaces a built-in set of the same number;
  `Remove` deletes downloads, hides built-ins). `Sandbox.FindPart` falls back to `PartStore.Find`; `HasPart` doesn't load.
  Typing uses `VirtualKeyboard.cs` (the game's own laser-clicked key panel): TouchScreenKeyboard opened nothing from
  these screens on the Quest (Oct 23), even unsecured. (ModelMenu Rename still uses the system keyboard.)
  `Sandbox.StartMenuVersion` lets the menu redraw only while one of its screens is showing. Offline check (Oct 2026):
  zip vs disk parsing identical for 147 parts; 21330 Home Alone: 344 of 361 parts made in 1.2 s on the PC.
- `InstructionBook.cs` + `Assets/Plugins/Android/PdfBridge.java`: PDFs picked via ACTION_OPEN_DOCUMENT
  (headless android.app.Fragment) are copied to `persistentDataPath/Instructions/<set> booklet <n>.pdf`;
  pages rendered by PdfRenderer on a thread to `Instructions/cache/<booklet>/<page>.jpg`, results via
  UnitySendMessage to the Sandbox GameObject. Portrait pages show as a two-page spread, landscape alone.
  Several PDFs per set: booklets are ordered by the number in the file name (the order added); Next/Back run on
  into the neighbouring booklet; "Move Earlier" swaps the open booklet with the one before (files + cache folders).
  Android-only (app entry is GameActivity). Check Java with javac + android.jar + Unity classes.jar.
- Roadmap: A) Set Finder (done), B) Build It Yourself (written), C) instruction book (PDF pages vary
  between portrait and landscape; the book must adapt and be freely movable/tiltable): LEGO PDFs the player
  downloads in the Quest browser, imported via the Android file picker (Storage Access Framework,
  needs a small Java plugin in Assets/Plugins/Android) into app storage, rendered with Android's
  PdfRenderer, shown as a two-page floating book. Do NOT scrape lego.com/Rebrickable for PDFs
  (bot protection + terms); never commit PDFs.
- `Assets/Scripts/LDraw/`: `LDrawParser.cs` (pure C#, System.Numerics, testable with `dotnet`), `LibraryFiles.cs`,
  `PartBuilder.cs` (parsed data -> mesh + BrickPartData + set steps; shared by editor and headset), `PartStore.cs`.
  `Assets/Editor/LDraw/`:
  Tools > LDraw Part Importer window, which writes meshes + `BrickPartData` to `Assets/LDrawParts/`.

## Connections (non-stud snapping)
- `LDraw/shadow/` = git clone of github.com/RolandMelkert/LDCadShadowLibrary (CC BY-SA 4.0, gitignored).
  `Connections/` (committed) adds files in the same format for parts it misses (e.g. 6015 tyre, 6014a rim).
- `LDrawParser` collects `!LDCAD SNAP_CYL/CLP/FGR/GEN/INCL/CLEAR` metas through sub-files (CLEAR drops
  inherited ones) into `ConnectorData` (Unity space; shapes extend along the meta's -Y). Duplicates removed.
- `Connector.cs`: runtime `Connector` + `Connections.Fit/QuarterTurns/AreMated/Align`. Pegs fit holes of
  equal radius (R in R/S, A in R); clips take male R of their radius; hinge fingers (same group + radius) fit when
  their finger/gap patterns interlock centre to centre (`FingerPattern.Interlock`; profile "F<r>|<M/F>|<seq>", aligned
  middle to middle; FGR connectors are `Center` unless the meta says center=false, as LDCad does); generic connectors match same profile,
  opposite gender. Stud-like (R6/S6) pairs and axle-in-cross-hole (both have an A token) pairs snap to
  quarter turns; others spin freely. Non-sliding pegs enter a hole through the end they point toward
  (same direction: peg base at the hole's start; opposite: at the hole's far end). Sliding cylinder pairs
  (Technic pins/axles/holes/bars) click to half-stud steps from the fixed shape's start, or rest flush
  against a part already on the same shaft when within 3 mm (`SlideShift`, anchors from `PartnerEnds`).
  Steps line up each shape's reference face (`FaceOffset`): its start, except a hole whose length isn't a whole number
  of half studs, where it's the end with the wider section (e.g. the 18 LDU "R6 R8" holes of Technic frames
  32532b/32531a, 73109, fairings 2395/2403: 2 LDU collar recess at the outer face only).
  Physical stops: connectors carry `sections` ("R8:2 R6:16 R8:2", from the shadow `secs`, written at import);
  `Connections.Fouls` rejects slide positions where a wider peg section would sit in a narrower hole section (a
  pin's collar in the bore rather than a recess); `SlideShift` then takes the nearest good spot: a step, resting
  against a neighbor, or seated edge to edge. Parts imported before this have no sections and no stops.
  With parts already on the shaft, the step grid is counted from the nearest neighbour face (flush or whole half
  studs away); a neighbour may be overlapped by at most 0.2 mm (`Blocked`). A single spinning connection (one pin)
  is squared up with the part it plugs into when within 20° (`SquareTurn`, applied to the final pose only, so
  two-pin fits still win). The roll search also turns about a pin so crosswise connectors (studs on a part hanging
  on a sideways pin) stand exactly in line (`PerpendicularReach`), and scoring charges for plugged-in connections
  that are out of line (`CountConnections` misfit, `MisfitCost` 1 mm per degree or mm): studs are accepted up to
  ~5° off, and without this a 73109 on a frame pin settled 2.3° tilted (Oct 14 rebuild save) and passed the tilt on.
  A denser plate+half-stud grid from every anchor was tried and made placement
  inconsistent; avoid it.
  Clips slide freely. Single-connection snaps prefer centering the part over the connector. A connector counts as taken only where an existing
  partner occupies the same span along it (`Connections.SpanAlong`), so axles/bars/pins hold several parts.
  `Brick.FindSnap`: aligns each candidate pair, keeps the 6 closest, then also tries rolls about the
  connection that line up a second connector, scoring by distance/turn minus 2 mm per extra connection
  plugged in (fixes studs on free-spinning pins, e.g. a 3x3 round tile on 4 half-pins in a gear). The roll search
  scans every free parallel connector nearby (sorted by distance from the axis), not just those close to the first
  connection (a 5 cm limit left assemblies on widely spaced pins snapped by one pin only). Snap distance (`move`) is
  measured at our connection point (`SnapCandidate.anchor`), not the brick origin, so a small corrective turn of a
  big group isn't rejected as a long move. A tolerance for slightly-off pins (2.5 mm) was tried and removed at the
  owner's request: snaps stay exact; a model built off-grid by older bugs should be rebuilt (Cobra save, Oct 2026:
  door pins 390 LDU apart, nearest car hole pair 1.6 mm closer; 59 studs 0.5-2.8 LDU short of home). Snaps are rejected
  only if a connector they'd use is already mated with a third part (`UsesTakenConnection`); box overlap
  checks were dropped for snapping because part bounding boxes are wrong for clips/brackets/car bases.
  Parts with upward R6 studs but no upward R6/S6 sockets anywhere get the estimated footprint grid added at import
  (`AddMissingBottomSockets`; a centred "jumper" stud doesn't shift that grid). Bars (R4) fit axle holes (A6). Brick snapping, group carrying, and group free-connector lists all use
  connectors; parts without data fall back to `studs`/`antiStuds`.

## Start Over and performance
- Start menu "Start Over" (click twice) reloads the scene; `UndoHistory.Clear()` first, and
  `BrickGrabInput` re-enables trigger grabbing on every `sceneLoaded`. Saved models/PDFs/settings persist.
- `PerformanceHud.cs` (Settings > Performance readout): fps + slowest frame, parts, triangles, draw calls
  (ProfilerRecorder render counters, else an estimate from visible renderers, marked "est.").
- Snapping speed: `ConnectorGrid` files each connector under every cell along its length and queries walk along the
  querying connector with a small margin (never size a query by the longest connector nearby: a 26-stud hose made
  every lookup sweep thousands of cells and dropped the Cobra to ~15 fps). HUD shows the slowest snap search (ms).
  `Brick.CollectNearby` puts nearby connectors into two `ConnectorGrid`s (spatial hash, 16 mm cells):
  free ones and fully used ones (a stud with a brick on it); only nearby grid cells are compared. Each brick caches its
  world connectors and used/partly-used flags (`UpdateConnectorUse`), redone when `s_layoutVersion` changes
  (`RefreshLayout`: a resting brick's `transform.hasChanged`, bricks appearing/going, or a different held set; "used"
  only counts partners moving with it). Held groups show one combined ghost mesh (`BuildGroupGhost`, a submesh per
  color); the ghost search is skipped while the hand is still. Colors use shared per-color materials
  (`BrickPartData.SharedMaterial`), no MaterialPropertyBlocks, so the SRP Batcher works.
- `BrickMerger.cs` (Settings > Fast drawing): bricks still for 0.4 s are drawn as combined chunk meshes (128 mm cells
  within their parent: world, the held root for a carried group, a set build root; one submesh per shared material)
  and their own MeshRenderers are disabled. Bricks stay real objects. A moved/changed/held brick (`Leave`) un-merges
  its chunk at once; chunks re-merge after 0.15 s quiet, 150k vertices per frame. See-through, preview, stored and
  held bricks are never merged. Only the merger may toggle a brick's MeshRenderer.enabled; always clone the prefab,
  never a live brick (its renderer may be off). Part meshes must stay readable (`m_IsReadable`).
- Scaling plan, remaining: 1) (done) one shared material per color instead of MaterialPropertyBlocks so the
  SRP Batcher works, 2) (done, BrickMerger) merge idle models into combined meshes, 3) skip covered
  studs/hidden detail + LOD, 4) (done) spatial hash for connectors, 5) foveated rendering / dynamic resolution.

## Undo (`UndoHistory.cs`)
- Carried groups keep partly used connectors (`BuildGroupConnections`: used only if covered end to end or a point),
  so a pin half inside the group can still plug in. `_groupIndex` (all group connectors, root-local) feeds
  `OwnSpans`, passed to `Connections.Align` as `ownSpans`; `OverOwn` stops the fixed shape sliding over the stretch
  the moving model already occupies, and its ends are extra seating candidates.
- Two hands: a grab never takes anything out of the other hand. `CollectGroup(otherHand)` only gathers loose pieces
  the other hand was carrying (`GiveUp` hands them over); a locked piece of the model in the other hand is pinned in
  place (`_pinnedTo`, re-applied in LateUpdate) and the grab does nothing; locked pieces never pose.
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
- Underside sockets are inferred as a stud grid over the part's footprint (LDraw doesn't mark them), each at the
  lowest flat face around that cell with room above for a stud (`UndersideAt`); empty or solid/curved cells get none.
  Car base 3385 and mudguard 3387 have hand-made socket files in `Connections/parts`.
  Guessed sockets are added whenever a part has no upward stud sockets and either has top studs or is a stud-built
  kind by description (`BuildsByStuds`: not Minifig/Technic/Tyre/Wheel/...); if the footprint grid finds none, spots
  half a stud off it are tried (centre socket under a dish). Stepped undersides (cutout slopes) are real, not errors.
- Tyres and rims pair by the sizes in their LDraw names (`AddWheelConnection`: "Tyre W/A x D" + "Wheel Rim W x D" ->
  generic `rim<W>x<D>` at the bounds centre on the axle line), unless the part already has a generic connector.
- Parts audit (Oct 2026, 861 parts): still without connectors = minifig hair/beards/capes/swords/tools and the
  train traction band 85544 (a rubber ring; what it wraps in set 10274 is unknown). Hand files added for glass 60602
  (frame 60593's slot), bar 7307 (two long bars + four uprights), finger 3171 (clip at origin). Plate 79387 (4x4x2/3
  rounded, 2x2 cutout): the shadow gives only the 4 corner sockets (corner feet, 16 LDU down); the 8 edge ones are in
  Connections, 8 LDU up in the plate-deep recess between the feet.
- The headset app is installed as com.UnityTechnologies.com.unity.template.urpblank (the build profile overrides
  ProjectSettings' com.michelseth5.brickbuilder). Saves: /sdcard/Android/data/<that>/files/Models (adb pull works;
  run-as doesn't, the build isn't debuggable). Changing the package name would install a separate app without the saves.
- Scripts can be test-compiled outside Unity with a netstandard2.1 csproj referencing
  `<Unity>/Editor/Data/Managed/UnityEngine/*.dll` and `Library/ScriptAssemblies/*.dll`.
