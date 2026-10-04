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
- `BrickTray.cs` + `PlaceInFrontOfUser.cs`: spawns parts (from `BrickCatalog`) in rows in front of the user.
- `RoomSurfaces.cs`: upward-facing MRUK plane lookup.
- `Assets/Editor/LDraw/`: `LDrawParser.cs` (pure C#, System.Numerics, testable with `dotnet`) and
  Tools > LDraw Part Importer window, which writes meshes + `BrickPartData` to `Assets/LDrawParts/`.

## Notes
- LDraw -> Unity: (x, -y, z) * 0.0004 m; front-facing LDraw polygons are reversed for Unity winding.
- Underside sockets are inferred as a stud grid over the part's footprint (LDraw doesn't mark them).
- Scripts can be test-compiled outside Unity with a netstandard2.1 csproj referencing
  `<Unity>/Editor/Data/Managed/UnityEngine/*.dll` and `Library/ScriptAssemblies/*.dll`.
