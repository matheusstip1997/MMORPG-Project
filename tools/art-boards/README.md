# Art reference boards: the scripts

These scripts build the art reference boards in `docs/design/` (`reference-boards-w1/w2/w3`, `reference-board-w4`, `reference-board-c1/c2/c3` and `reference-board-figures`, each a `.md` and a `.svg`). They compute every table and draw every drawing from the inputs in `boards/`.

They are **not game code**: nothing here is built, shipped or read by the client or the server. They need Windows PowerShell 5.1 and nothing else. Looking at a drawing needs Microsoft Edge.

## Commands

Run from the repository root.

```
powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1              # rebuild every board
powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1 -Board c2,w3 # rebuild only these
powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1 -Check       # compare, change nothing
```

`-Check` builds into a temp folder and compares the result with `docs/design/`. It prints `same` or `DIFFERENT` for each file and exits with 1 when any file differs. Line endings are ignored.

## The boards are generated

**Do not edit a board's `.md` or `.svg` in `docs/design/` by hand.** Change its inputs and rebuild, and commit the inputs and the outputs together. A hand edit makes `-Check` fail and is lost at the next build.

`docs/design/reference-boards-questions.md` is written by hand and is not built here.

## Layout

| Path | What it is |
|---|---|
| `Build-ArtBoards.ps1` | Builds or checks the boards. It knows which script builds which board. |
| `BiomeBoard.ps1` | Builds a set of biome boards (W1, W2, W3, W4). |
| `CityBoard-C1.ps1`, `-C2.ps1`, `-C3.ps1` | Build one city board each. Their drawings (a section and a plan to scale) are code. |
| `FigureBoard.ps1` | Builds the characters and monsters board. It reads the camera values and the palettes of the biome boards. |
| `Common.ps1` | Shared: the colour math, the warning blend, the photo lines, writing the files. |
| `boards/<id>/palette.json` | The swatches and what the board computes from them. |
| `boards/<id>/template.md` | The board's text, with `{{PLACEHOLDER}}` where a computed table or list goes. |
| `boards/<id>/photos.json` | The photo links of the board, by section. |
| `Find-PhotoRefs.ps1` | Searches Wikimedia Commons and writes a list and a contact sheet of thumbnails. |
| `Render-Page.ps1` | Renders an SVG or HTML file to a PNG with headless Edge, to look at it. |

## Changing a board

1. Edit `boards/<id>/palette.json` (a colour), `template.md` (the text) or `photos.json` (a photo link).
2. Rebuild it: `Build-ArtBoards.ps1 -Board <id>`.
3. Read the changed tables. **The prose in `template.md` quotes numbers from them** (for example "8 from the roof"), and nothing checks that. After a colour change, find the old numbers in the template and correct them.
4. Look at the drawing: `Render-Page.ps1 -Path docs/design/<name>.svg -OutPng <somewhere outside the repository>`.
5. Run `-Check` for all boards. W3 reads the palettes of W1 and W2 for its table of every biome, so a change there changes W3 too.

## The method

The numbers are colour differences (CIE76) between flat colours, HSV saturation, the attack warnings blended over a ground the way the client does it today, and a colour-vision simulation. The exact steps, constants and matrices are written out in `docs/design/reference-boards-w1.md`, "The exact method", and implemented in `Common.ps1`. Keep the two in step.

The warning colours and alphas are copied into each `palette.json` (`telegraphs`) from `data/tables/HudThemeTokens.json` as they were on 2026-11-10, with `baselineGround` as the ground the percentages compare against. They are not read from the table: when the table changes, update the palettes and rebuild.

## `palette.json`

Common to every board:

| Field | Meaning |
|---|---|
| `output` | The file name in `docs/design/`, without extension. |
| `telegraphs` | The warning colours: `name`, `hex`, `alpha` (a hex byte). `none` is the uncoloured area. |
| `baselineGround` | Today's M1 greybox ground. Percentages are relative to the warnings on it. |

Biome boards (`BiomeBoard.ps1`):

| Field | Meaning |
|---|---|
| `title`, `subtitle` | The heading of the drawing. |
| `kit` | Kit swatches shown at the top of the drawing and listed by `{{KIT}}`. |
| `roofs` | Optional. Kit roofs to compare with each biome's ground (`{{KITCHECK}}`). Without it, the first `kit` swatch is the one roof. |
| `kitSpacing`, `kitNote` | Optional. The spacing of the kit chips and a note beside them. |
| `alsoBoards` | Optional. Other boards whose biomes join this one's in `{{ALLGROUNDS}}`. |
| `pairs` | Optional. A list of `a` and `b`, two swatch names of this board. `{{EDGE}}` gives the difference between each pair. |
| `warningAgainst` | Optional. A list of `ground` and `swatch`, two swatch names. `{{WARNVS}}` gives each coloured warning on that ground against that swatch. |
| `biomes[]` | `key` (used in placeholders), `id`, `name`, `mood`, `light` (three swatches), `swatches`, and optionally `road` (the name of the road swatch, for `{{ROAD}}`) and `kitHere` (the settlement and its kit; `no settlement` leaves the roof cells empty). `settlement` set to true marks a block that is a settlement and not a biome; it is left out of `{{ALLGROUNDS}}`. |
| `biomes[].swatches[]` | `name`, `hex`, `role` (`ground`, or any other word such as `prop`, `accent`, `water`, `lava`, `roof`) and `share` (percent of the screen; the shares of one block add up to 100). The warning check runs on every `ground` swatch. |

City boards:

| Field | Meaning |
|---|---|
| `swatches[]` | The kit: `name`, `hex`, `role`, and `use` (where it is used). The warning check runs on every `ground` swatch. |
| `around[]` | Swatches of the biome around the city, taken from its biome board, to compare the kit with. |

A city script looks swatches up **by name** for its tables and drawings. Renaming or removing a swatch means changing the script as well.

The characters and monsters board (`FigureBoard.ps1`, `boards/figures`) has no `telegraphs`:

| Field | Meaning |
|---|---|
| `camera` | The camera values the sizes are computed from: `distanceCm`, `lookAtHeightCm`, `fieldOfViewDegrees`, `pitchDegrees`, `screenHeightPx`. They are copied from `data/tables/ClientCamera.json` as they were on 2026-11-10. The script reads that table and prints a warning when a value no longer matches; update the copy and rebuild. |
| `smallScreenHeightPx` | The second screen height of the size table. |
| `character`, `positions[]` | The character's height, width and head (`headCm`, which is also the head of the drawn figures), and the places on screen (`zCm`, away from the camera) where its size is given. |
| `sizes[]` | Rows of the size table: `type` is `tall` (`fromCm` to `toCm` above the ground) or `wide` (`cm` across, at `atCm` above the ground). |
| `monsters[]`, `sizeGround`, `sizeFigure` | The footprints drawn at pixel scale, the ground behind them, and the class drawn beside them. The smallest and the largest radius are those of `data/tables/MonsterDef.json` on 2026-11-10; the script warns when they no longer match (then update `monsters[]` and the monster rows of `sizes[]`). |
| `classes[]` | The schematic figures: `id`, `name`, `armour`, `carries`, `weapon` (`swordShield`, `bow`, `staff` or `gloves`; a new one needs code) and the widths of the shape in metres. |
| `biomeBoards` | The biome boards whose main grounds are read. |
| `outlines[]`, `tones[]` | The outline colours and the figure tones compared with every main ground. |
| `families[]`, `biomes[]` | A tone per monster family (none for a family made of its biome's material), and each biome's signature and shared families. Every biome of the biome boards needs a row. |
| `middleGround` | The biome shown between the lightest and the darkest in the outline drawing. |

## `template.md` placeholders

- Biome boards: `{{SAT}}`, `{{BASELINE}}`, `{{CVD}}`, `{{KIT}}`, `{{KITCHECK}}`, `{{ROAD}}`, `{{ALLGROUNDS}}`, `{{EDGE}}`, `{{WARNVS}}`, and for each biome key `{{PALETTE:key}}`, `{{LIGHT:key}}`, `{{CHECK:key}}`.
- City boards: `{{PALETTE}}`, `{{ROOF}}`, `{{EDGE}}`, `{{CHECK}}`; C3 also has `{{RED}}`, `{{REDCMP}}`, `{{COVER}}` and `{{LITSHADE}}`.
- The characters and monsters board: `{{SIZES}}`, `{{DEPTH}}`, `{{SHOULDERS}}`, `{{OUTLINE}}`, `{{TONES}}`, `{{FAMILIES}}`, `{{HOME}}`, and the single numbers `{{PXPERM}}`, `{{CMPERPX}}`, `{{CHARPX}}` and `{{CHARWPX}}`.
- Every board: `{{REFS:section}}` for each section of `photos.json`.

A placeholder that the script does not fill stops the build.

## Photos

The boards **link** to photos and store none (designer decision of 2026-11-10, Q33 in `reference-boards-questions.md`). So nothing is added to `THIRD_PARTY_LICENSES.md`.

- Use only files under a free licence that allows commercial use (public domain, CC0, CC BY, CC BY-SA), and state the author and licence as the source states them.
- Look at a photo before listing it.
- An entry of `photos.json` is `title` (the link text), `url` (the file's page), `author`, `licence` and `note` (what the photo shows for the board). A board line reads `- [title](url): note. author, licence.`

`Find-PhotoRefs.ps1` helps to find candidates on Wikimedia Commons:

```
powershell -ExecutionPolicy Bypass -File tools/art-boards/Find-PhotoRefs.ps1 -OutDir <folder outside the repository> -QueryFile <queries.json>
powershell -ExecutionPolicy Bypass -File tools/art-boards/Render-Page.ps1 -Path <folder>\sheet.html -OutPng <folder>\sheet.png -Width 1260 -Height <30 + topics * 165> -PageHeight 1320
```

It sends the search words to Commons with a generic client name and no personal data. Commons limits anonymous requests, so the script waits between searches; a long list takes minutes.
