# Quest Brick Builder

A mixed-reality brick builder for Meta Quest 3: build with real-scale virtual bricks on your real table, in
passthrough. Parts snap the way the real ones do (studs, Technic pins and axles, bars and clips, hinges, ball
joints), and you can build real sets step by step, build them your own way from a parts bin, or free-build.

**New here? See the [User Guide](USER_GUIDE.md)** for controls, adding sets with your Rebrickable key, instruction
PDFs, fixing parts that don't snap, and more.

## Features

- **Snapping** using the LDCad shadow library's connection data: studs and sockets, Technic pins and axles,
  bars and clips, hinges (posable with two hands), ball joints.
- **Build a Set**: follow a set's building steps with ghost previews (sets with LDraw instructions).
- **Build It Yourself**: a parts bin for a set, filtered by type and color, plus an instruction book that shows
  PDF instructions you've downloaded yourself.
- **Get New Sets** in the game: search Rebrickable and add a set; its parts are made on the headset from the
  LDraw library (a one-time ~236 MB download).
- **Fix Snapping**: an in-game editor for a part's connection points, for parts whose data is missing or wrong.
- **Locking** models into solid pieces (X button, or automatically), so 1,000s of pieces stay fast and firm.
- Saving and loading models, wrist storage, undo, duplicating, and more.

## Requirements

- Meta Quest 3 (or 3S), with developer mode on.
- Unity **6000.6.4f1** (Unity 6) with Android Build Support, via Unity Hub.
- To add sets in the game: a free [Rebrickable](https://rebrickable.com) account and API key (Settings >
  Rebrickable key in the game).

## Getting started

1. Clone this repository and open the folder in Unity Hub (**Add > Add project from disk**).
2. Open `Assets/Scenes/SampleScene`.
3. Plug in the Quest, then **File > Build Profiles**, pick **Meta Quest**, and **Build And Run**.

The parts already imported are in `Assets/LDrawParts`, so that's all you need to play. To import more parts in
the editor (**Tools > LDraw Part Importer**, **Tools > Set Finder**), also set up:

- the [LDraw parts library](https://library.ldraw.org): unzip `complete.zip` into `LDraw/` next to `Assets`
  (and `ldrawunf.zip` into `LDraw/unofficial` for unofficial parts);
- the [LDCad shadow library](https://github.com/RolandMelkert/LDCadShadowLibrary): clone it into `LDraw/shadow`;
- for Tools > Set Finder, your Rebrickable API key in `Secrets/rebrickable.key` (that folder is never committed).

`CLAUDE.md` describes how the code is organised and how the trickier parts work.

## Controls

- **Trigger**: pick up one piece. **Grip**: pick up the whole model.
- **Thumbstick**: turn what you're holding a quarter turn.
- **Y**: parts menu (and Save, Load, Fix Snapping). **B**: colors. **A**: duplicate what you're holding.
- **X**: lock the model you're holding or touching (again to unlock the newest pieces).
- **Left thumbstick click**: undo. **Menu button**: start menu (sets, settings).

## License

The code is under the [MIT license](LICENSE). Part shapes, connection data and set models come from the LDraw and
LDCad communities under their Creative Commons licenses; see [LICENSE](LICENSE) and [CREDITS.md](CREDITS.md).

LEGO® is a trademark of the LEGO Group, which does not sponsor, authorize, or endorse this project.
