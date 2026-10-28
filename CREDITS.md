# Credits

## LDraw parts library

Brick shapes are generated from the [LDraw](https://www.ldraw.org) parts library, used under the
[Creative Commons Attribution 4.0 license (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/) (older parts:
[CC BY 2.0](https://creativecommons.org/licenses/by/2.0/)). Thanks to the LDraw.org community and part authors.
The part meshes made from it are in `Assets/LDrawParts`; a player who downloads sets in the game gets the library
straight from library.ldraw.org.

The library itself isn't stored in this repository; download `complete.zip` from
https://library.ldraw.org and unzip it into an `LDraw` folder next to `Assets`.

Parts not yet in the official release (e.g. some wheels and tyres) come from the LDraw unofficial parts
library (`ldrawunf.zip` from https://library.ldraw.org, same license), unzipped into `LDraw/unofficial`.
Unofficial parts may still change before release.

## Connection data

How parts connect (studs, pins, holes, axles, bars, clips, hinges) comes from the
[LDCad shadow library](https://github.com/RolandMelkert/LDCadShadowLibrary) by Roland Melkert and
contributors, used under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). It isn't stored
in this repository; clone it into `LDraw/shadow`. Extra connection files for parts it doesn't cover are in
`Connections/`, under the same license. Connection data derived from these (in `Assets/LDrawParts`) is
likewise CC BY-SA 4.0.

## Set models

Set files in `Sets/` come from the [LDraw Official Model Repository](https://library.ldraw.org/omr),
also under CC BY 2.0:

- `4914-1.mpd` — Fire Chief's Car (4914), modeled by Marc Giraudet [Mad_Marc]
- `10214-1.mpd` — Tower Bridge (10214), modeled by Orion Pobursky [OrionP]

## Parts lists

Set parts lists (for Build It Yourself and sets downloaded in the game) come from
[Rebrickable](https://rebrickable.com), through its API. Each player uses their own Rebrickable API key.
Rebrickable's terms don't allow its content to be used to train AI models; that applies to the parts lists here
(in `Assets/Resources/Sets`) too.

## Unity resources

`Assets/TextMesh Pro` holds Unity's TextMesh Pro resources (Unity Companion License); its Liberation Sans font is
under the [SIL Open Font License 1.1](https://openfontlicense.org).

## This project's code

The code is under the MIT license (see `LICENSE`).

LEGO® is a trademark of the LEGO Group, which does not sponsor, authorize, or endorse this project.
