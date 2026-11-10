# Art reference boards: the scripts

These scripts build the art reference boards in `docs/design/` (`reference-boards-w1/w2/w3` and `reference-board-c1/c2/c3`, each a `.md` and a `.svg`). They compute every table and draw every drawing from the inputs in `boards/`.

They are **not game code**: nothing here is built, shipped or read by the client or the server. They need Windows PowerShell 5.1 and nothing else. Looking at a drawing needs Microsoft Edge.

## Commands

Run from the repository root.

```
powershell -ExecutionPolicy Bypass -File tools/art-boards/Build-ArtBoards.ps1              # rebuild all six boards
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
| `BiomeBoard.ps1` | Builds a set of biome boards (W1, W2, W3). |
| `CityBoard-C1.ps1`, `-C2.ps1`, `-C3.ps1` | Build one city board each. Their drawings (a section and a plan to scale) are code. |
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
| `biomes[]` | `key` (used in placeholders), `id`, `name`, `mood`, `light` (three swatches), `swatches`, and optionally `road` (the name of the road swatch, for `{{ROAD}}`) and `kitHere` (the settlement and its kit; `no settlement` leaves the roof cells empty). |
| `biomes[].swatches[]` | `name`, `hex`, `role` (`ground`, `prop`, `accent` or `water`) and `share` (percent of the screen; a board's shares add up to 100). The warning check runs on every `ground` swatch. |

City boards:

| Field | Meaning |
|---|---|
| `swatches[]` | The kit: `name`, `hex`, `role`, and `use` (where it is used). The warning check runs on every `ground` swatch. |
| `around[]` | Swatches of the biome around the city, taken from its biome board, to compare the kit with. |

A city script looks swatches up **by name** for its tables and drawings. Renaming or removing a swatch means changing the script as well.

## `template.md` placeholders

- Biome boards: `{{SAT}}`, `{{BASELINE}}`, `{{CVD}}`, `{{KIT}}`, `{{KITCHECK}}`, `{{ROAD}}`, `{{ALLGROUNDS}}`, and for each biome key `{{PALETTE:key}}`, `{{LIGHT:key}}`, `{{CHECK:key}}`.
- City boards: `{{PALETTE}}`, `{{ROOF}}`, `{{EDGE}}`, `{{CHECK}}`; C3 also has `{{RED}}`, `{{REDCMP}}`, `{{COVER}}` and `{{LITSHADE}}`.
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
