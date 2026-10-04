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
- `Sets/` holds OMR set files (.mpd); the importer's "Import Set Parts" imports every part a set uses.
- Roadmap (agreed order): 1) parts that scale (done: set import, colors, menu paging),
  2) "Build a Set" mode: step-by-step ghosts, place parts on ghosts (works for non-stud connections),
  start menu with modes, 3) Rebrickable set search/part lists.
- `Assets/Editor/LDraw/`: `LDrawParser.cs` (pure C#, System.Numerics, testable with `dotnet`) and
  Tools > LDraw Part Importer window, which writes meshes + `BrickPartData` to `Assets/LDrawParts/`.

## Notes
- LDraw -> Unity: (x, -y, z) * 0.0004 m; front-facing LDraw polygons are reversed for Unity winding.
- Underside sockets are inferred as a stud grid over the part's footprint (LDraw doesn't mark them).
- Scripts can be test-compiled outside Unity with a netstandard2.1 csproj referencing
  `<Unity>/Editor/Data/Managed/UnityEngine/*.dll` and `Library/ScriptAssemblies/*.dll`.
