# Quest Brick Builder: User Guide

Build with real-scale virtual bricks on your real table, in passthrough. This guide covers the controls, building,
locking, saving, getting new sets (with your Rebrickable key), instruction PDFs, and fixing parts that don't snap.

New to the game? Read **First launch**, **Controls** and **Building**, then come back to the rest as you need it.

## Contents

1. [First launch](#1-first-launch)
2. [Controls](#2-controls)
3. [Building](#3-building)
4. [Locking models](#4-locking-models)
5. [Saving and loading](#5-saving-and-loading)
6. [Your Rebrickable API key](#6-your-rebrickable-api-key)
7. [Getting new sets](#7-getting-new-sets)
8. [Build a Set (step by step)](#8-build-a-set-step-by-step)
9. [Build It Yourself (parts bin)](#9-build-it-yourself-parts-bin)
10. [Instruction PDFs](#10-instruction-pdfs)
11. [Fixing parts that don't snap](#11-fixing-parts-that-dont-snap)
12. [Removing sets](#12-removing-sets)
13. [Settings](#13-settings)
14. [Tips and troubleshooting](#14-tips-and-troubleshooting)

---

## 1. First launch

- The first time, the game asks to use your **room scan** (so bricks can sit on your real tables and floor). Allow it.
  If your room hasn't been scanned, the floor is used.
- The **start menu** opens in front of you. You can always bring it back with the **menu button** on the left
  controller (the one with three lines).
- Menus are used by pointing the **laser** from either controller and pulling the **trigger**. Text that's too
  long for its button ends in "…" and scrolls when you point at it.
- Every floating menu has a **handle** underneath: grab it to move the menu, and an **X** in its corner to close it.

**Start menu:**

| Button | What it does |
| --- | --- |
| Free Build | Close the menu and build freely with any part (Y opens the parts menu). |
| Build a Set | Build a set step by step, following ghost previews (sets with step-by-step instructions). |
| Build It Yourself | Get a set's parts in a bin and build it your own way, with its PDF instructions beside you. |
| Get New Sets | Search Rebrickable and add sets to the game. |
| Settings | Options (see [Settings](#13-settings)). |
| Start Over | Clear the room and start fresh (click twice). Saved models, sets, PDFs and settings are kept. |

## 2. Controls

| Control | Action |
| --- | --- |
| **Trigger** (index finger) | Pick up one piece. On menus: click. |
| **Grip** (middle finger) | Pick up the whole model the piece is part of. |
| **Thumbstick** (on the hand that's holding) | Turn what you're holding a quarter turn: left/right spins it flat, up/down tips it toward or away from you. Let the stick return to the middle between pushes. |
| **Y** | Parts menu: parts by category, baseplate, trash can, undo, save, load, Fix Snapping, color. |
| **B** | Color menu. |
| **A** | Duplicate what you're holding (a copy appears just above it). |
| **X** | Lock the model you're holding (or touching with the left controller). Press again to unlock the newest pieces. See [Locking](#4-locking-models). |
| **Left thumbstick click** | Undo (let go of everything first). |
| **Menu button** (left controller) | Start menu. |

Hand tracking works too: **pinch** to pick up one piece, close your **whole hand** (palm grab) to pick up a model.

**The light on your controller** (while you're holding something):
green = nothing locked, red = all locked, amber = a locked model with some loose pieces.

## 3. Building

- **Getting parts:** press **Y**, pick a category, then grab a part from the grid. Pick its color first with **B**
  (or the color button at the bottom of the Y menu).
- **Snapping:** move a piece close to where it goes. A **see-through ghost** shows where it will snap; let go and it
  clicks into place. Studs, Technic pins and axles, bars and clips, hinges and ball joints all snap like the real
  thing. Pins and axles click along in half-stud steps.
- **Turning:** turn your hand, or push the thumbstick of the holding hand for exact quarter turns.
- **Moving a model:** grip any piece to carry the whole model; trigger to pull one piece off it.
- **Two hands:** hold a model in one hand and grab another piece with the other to attach it. To **pose a hinge or
  joint**, hold the model with one hand and grip the moving part with the other, then turn it. (Settings > Joints
  can make joints click every 15° or 45°.)
- **Setting things down:** let go near a table or the floor and it rests there; let go in the air and it floats.
- **Baseplate:** Y > Get Baseplate. **Trash:** Y > Get Trash Can, then drop pieces into it.
- **Wrist storage:** let go of a piece (or a model) right next to your **other wrist** and it shrinks onto that
  forearm. Identical items stack with a count (x3). Grab one to take it back out at full size.
- **Undo:** left thumbstick click, or Y > Undo (let go first).

## 4. Locking models

Big models stay fast and solid when they're **locked**: their pieces become one object that can't be knocked apart.

- **X** locks the loose pieces of the model you're holding (or touching with your left controller). If you're
  holding it, it locks when you put it down.
- **X again** (with nothing loose) unlocks the pieces you locked most recently. What comes loose flashes:
  green for a lone piece, yellow for pieces still joined together.
- **Auto-lock** (on by default): pieces you've added to a model lock by themselves a few seconds after you let go.
  A piece on its own stays loose. Change the delay or switch it off in Settings.
- **Locked groups:** two locked models snapped together stay two groups, so you can take them apart again: a
  **trigger** grab takes just the group under your finger; a **grip** takes everything. To make them one, press
  **X** on the model when nothing is loose: "Joined N locked groups into one".
- **Red markers** after locking point at studs or pins that aren't quite plugged in, so you can fix them early
  (switch off in Settings).
- Locked pieces can't be duplicated, stored on a wrist or trashed. Unlock them first.

Tip: lock often (or keep auto-lock on). Hundreds of loose pieces in one model slow everything down.

## 5. Saving and loading

- **Save:** Y > **Save Everything** saves everything in the room and on your wrists.
  - After you've loaded or saved a model, Save Everything asks whether to **Replace** that save or **Keep It and
    Save a New Model**.
  - Saving during **Build It Yourself** also saves your place: the set's bin (minus the parts you've used) and your
    instruction page come back when you load it.
- **Load:** Y > **Load Model** shows your saves with spinning previews. **Load** brings the model in front of you
  (a bar shows the progress), **Rename** gives it a name, **Delete** (click twice) removes it.
- Saves live on the headset, in the app's storage.

## 6. Your Rebrickable API key

The game gets set parts lists from [Rebrickable](https://rebrickable.com), a free site that catalogues LEGO® sets.
You need your own (free) API key to add new sets.

**Getting a key** (in any browser, including the Quest's):

1. Make a free account at [rebrickable.com](https://rebrickable.com) and sign in.
2. Open your account's **Settings** page and go to the **API** section.
3. Generate a new API key there and copy it (a long string of letters and numbers).

**Entering it in the game:**

1. Start menu > **Settings** > **Rebrickable key: Enter**
   (or Start menu > **Get New Sets**, which asks for it the first time).
2. A keyboard appears in front of you. Type the key with the laser (**Shift** for one capital letter, **Delete**
   to fix a mistake), then click **Done**.

The key is stored only on your headset. To change it later: Settings > **Rebrickable key: Change**.

## 7. Getting new sets

1. Start menu > **Get New Sets**.
2. **The first time**, download the **parts library** (about 236 MB, once). Use good Wi-Fi; a bar shows the progress.
3. Click **Search Sets**, type a set's name or number (e.g. `cobra` or `10357`) and click **Done**.
4. Results come seven at a time (**More Results** / **Previous Results**). Each shows the set number, piece count,
   year, and:
   - **step-by-step**: it has LDraw building instructions, so it works in **Build a Set** and **Build It Yourself**;
   - **parts list only**: it works in **Build It Yourself** (use the LEGO PDF instructions alongside).
5. **Click a set twice** to add it. A bar shows the parts list coming in and the parts being made on the headset.
   Big sets take a while.
6. When it's done, the set is under **Build a Set** and/or **Build It Yourself**.

A few parts (mostly printed minifigure pieces) don't exist in the LDraw library. They're swapped for plain versions
where possible, otherwise left out; the message after adding says how many.

## 8. Build a Set (step by step)

1. Start menu > **Build a Set** > pick a set.
2. **Ghosts** show where this step's parts go, and the **parts tray** holds them.
3. Take a part from the tray and let go of it close to its matching ghost (same part and color): it locks into
   place. When a step is done, the next one starts.
4. The panel has **Auto-place Step** (places the rest of the step for you), **Parts Again** (refills the tray),
   **Restart** and **Exit Set**. The build and the tray each have a handle to move them.

A finished build becomes one model: grip to carry it all, trigger to pull a piece off.

## 9. Build It Yourself (parts bin)

1. Start menu > **Build It Yourself** > pick a set. Its **parts bin** opens.
2. Each cell shows a real part with its **count first** ("3x Brick 1 x 2"). **Trigger** a part to take one;
   **grip** to take the whole stack (let go of a stack next to your wrist to store it as an xN slot).
3. **Filters** (top left): switch on **Type** and/or **Color** to get a column of each, and combine them
   (e.g. Plates + Red). With both off, every part shows. Used-up parts disappear.
4. Trashing a part from the set puts it back in the bin.
5. **Instructions** (top of the bin) opens the instruction book (see [Instruction PDFs](#10-instruction-pdfs)).
6. **Finish Model** joins everything you've built from the set into one model.
7. Lost the bin? Start menu > **Show Parts Bin**. Done? **Stop Building It Yourself**.

## 10. Instruction PDFs

The game shows LEGO instruction PDFs that **you** download. It doesn't fetch them for you.

**Getting the PDFs:**

1. In the **Quest's browser**, go to LEGO's official building instructions page (search "LEGO building
   instructions", or lego.com > Building Instructions).
2. Search for your set number and download its PDF (big sets have several booklets: download them all).
   They go to the headset's **Downloads** folder.

**Adding them to a set:**

1. Open the set in **Build It Yourself** and click **Instructions** at the top of the bin.
2. Click **Add PDF**. A list of PDFs in your Downloads appears; pointing at one shows a preview of its first page.
   Click the one for this set.
   - **The first time**, the game needs permission to read Downloads: click **Allow Access**, switch on
     **Allow access to manage all files**, then come back to the game.
   - Or click **Use File Picker Instead** to pick the file with the Quest's file picker.
3. For sets with **several booklets**, add each PDF in order (booklet 1 first). If you got the order wrong, open a
   booklet and click **Move Earlier**.

**Reading:**

| Button | What it does |
| --- | --- |
| `\|<` `<` `>` | First page, back, next (runs on into the next booklet). |
| Book | Switch booklet (shows "Book 2/3"). |
| Move Earlier | Move this booklet earlier in the order. |
| Add PDF | Add another booklet. |
| Rotate | Turn the pages a quarter turn (for landscape pages). |
| `-` `+` | Smaller / bigger. |
| X | Close the book. |

Grab the book's **handle** to move and tilt it anywhere. Portrait pages show as a two-page spread. The book
remembers your page for each set, and saving during Build It Yourself saves it too.

## 11. Fixing parts that don't snap

Most parts snap correctly, but some (especially in new sets) have connection points missing or in the wrong place.
You can fix them yourself:

1. **Hold the piece** in one hand. With the other, press **Y** and click **Fix Snapping**.
2. The piece appears **enlarged and see-through**, with every connection point as a colored marker:
   **green** studs/pins/axles/bars, **blue** sockets/holes, **orange** clips, **yellow** hinges, **purple** special.
   **X+ (red), Y+ up (white), Z+ (pink)** lines show the directions. **Grab the piece** to turn it any way you like.
3. **Pick a kind on the left** (Studs, Bars & Clips, Axles, Pins, Hinges, Special): its markers pulse and its
   buttons appear on the right.
4. **To add a point:** click **+ Stud**, **+ Socket**, **+ Bar**, **+ Hinge**, … then point at the piece where
   it goes (a preview follows the laser and clicks to the grid) and pull the trigger.
5. **To change one:** click its marker, then use **X−/X+/Y−/Y+/Z−/Z+** to nudge it (the **Grid** button sets the
   step: 1/2/5/10 LDU; a stud is 20 LDU across, a plate 8 tall), **Point Another Way**, **Longer/Shorter**, or
   **Delete**. For hinges, pick the **hinge kind**: it only snaps to its matching kind (e.g. 3 fingers with
   2 fingers).
6. **Save.** Every copy of that part, in every set, snaps with the new points straight away.
   **Restore Original** undoes your fixes for that part.

Tip: select a marker on the part it should connect to (in another Fix Snapping session) to see what kind it is.

## 12. Removing sets

Under **Build a Set** or **Build It Yourself**, click **Remove a Set**, then click a set **twice**. It also deletes
that set's instruction PDFs. Your saved models stay. Sets that came with the game are hidden rather than deleted;
Settings > **Bring back removed sets** brings them back (their PDFs need adding again).

## 13. Settings

| Setting | What it does |
| --- | --- |
| Ghosts | How see-through the snap ghost is. |
| Joints | Hinges and joints turn freely, or click every 15° / 45°. |
| Performance readout | Shows frame rate, parts, triangles and draw calls. |
| Fast drawing for big models | Draws still models as combined shapes (keep on). |
| Locked pieces as one object | Locked models become one solid object (keep on). |
| Mark pieces not fully plugged in | The red markers after locking. |
| Hide covered studs in locked models | Skips drawing studs nobody can see (keep on). |
| Bring back removed sets | Un-hides sets you removed. |
| Rebrickable key | Enter or change your key. |
| Auto-lock | Lock added pieces after 5 s / 15 s / Off. |
| Shadows | On / Off (Off is a little faster on big models). |

## 14. Tips and troubleshooting

- **Things feel slow or shaky with a big model:** lock it (X) or keep auto-lock on; switch Shadows off; check that
  Fast drawing and Locked pieces as one object are on. The Performance readout shows the frame rate.
- **A piece won't snap:** check its color and part against the instructions, try turning it a quarter turn with
  the thumbstick, and look for red markers on the model. If the part itself is wrong, use **Fix Snapping**.
- **A model came apart when I grabbed it:** you used the trigger on a model made of several locked groups. Use
  the grip, or press X on it once to join the groups.
- **Can't find a menu:** press the menu button (start menu) or Y (parts menu); grab a menu's handle to bring it
  closer.
- **Typing:** the game uses its own keyboard panel; click the keys with the laser.
- **Start Over** clears the room completely (click twice). Save first.

---

LEGO® is a trademark of the LEGO Group, which does not sponsor, authorize, or endorse this project.
Part shapes come from the [LDraw](https://www.ldraw.org) parts library and connection data from the
[LDCad shadow library](https://github.com/RolandMelkert/LDCadShadowLibrary); see [CREDITS.md](CREDITS.md).
