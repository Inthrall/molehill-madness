# Art sourcing

Where the pictures come from, what the two stores have to be told about them, and what a sheet has to look like before the importer will take it.

The audio doc's problem is redistribution: the good libraries are all free to use commercially and most of them are not free to keep in a public tree. Art has no equivalent trap, because these are generated outputs held outright rather than files licensed out of somebody's collection. What art has instead is a disclosure obligation to two stores that ask for different things, and a set of shape rules that decide whether a picture can become a sprite at all.

## The decision was already made

`art/` holds the generated sheets as they arrived. Two of the terrain textures carry the generator's watermark and `import-art.ps1` patches it out before it can tile across a sixty metre map. That is as plain a record as any that this game already ships AI-generated art, so whether to use a generator is not an open question. Adding a second tool changes which names go into the disclosure, not whether there is one.

The tool that made the existing sheets is not recorded anywhere in this repository. It needs to be before the first store submission, because both stores ask.

## What Higgsfield's terms say

| Question | Answer |
| --- | --- |
| Who owns the output | You do. Higgsfield does not claim ownership of inputs or outputs (Terms 4.4) |
| Commercial use | Permitted on every tier. There is no separate commercial licence to buy, and the rights survive cancelling the subscription |
| Their licence back | Non-exclusive, worldwide, royalty-free, to host and process. It ends when the content or the account is deleted |
| Training on what you upload | Yes, unless on an enterprise agreement. Deleting the content stops it going forward |
| Training on what it returns | Forbidden. Outputs may not be used to train, fine-tune or distil another model |

The fourth row is the one that matters for this project. Trialling on the existing assets means uploading this game's own art to a service that may train on it. Nothing in `art/` is secret and the repository is public in any case, so the cost is low, but it is worth making as a decision rather than by accident: the sheets that define this game's look become training material for a general model. Where that is unwanted for a particular asset, the answer is to not upload that asset, because there is no per-item opt out short of deleting it afterwards.

Every one of those rights attaches to the account that generated the output. This is a personal project and a commercial one, so the art has to be generated under the personal account, never a work one. Art made inside an employer's workspace leaves an ownership question that is tedious to unpick and impossible to unpick quietly, and on a business or enterprise workspace the uploads are visible to that workspace's administrators as well. The same reasoning already keeps this repository off the work GitHub identity.

## What the stores have to be told

The two are close to mirror images, and between them they cover everything.

| | Steam | Google Play |
| --- | --- | --- |
| What is declared | AI content in the game itself | Images and videos in the store listing and promotional material |
| Granularity | One checkbox and a short description | Per asset |
| When | The content survey, at submission | The upload flow, or the asset library later |
| What players see | A note on the store page | An AI label on each declared asset |
| In-game art | Declared here | Not addressed |
| Store art | Covered by the same disclosure | Declared here |

Valve's January 2026 revision took development tooling back out of it, so a code assistant is not disclosable and a generated sprite is.

One consequence is worth stating because it cuts against the obvious plan. Store graphics look like the risk-free place to point a new generator, and they are, as far as the importer is concerned. They are also the only place a player sees a per-asset AI label, because that is exactly what Play labels and in-game art is not.

## The rules for this repository

1. Generated art is allowed, in the game and in the store listing.
2. Generate under the personal account, never a work one. Ownership follows the account, and this game is not the employer's.
3. Source sheets go into `art/` exactly as they arrived, and stay there even when they are no longer what gets imported. Where a sheet cannot be cut as it stands, `build-sheet.ps1` lays its drawings out again beside it under a name ending `regrid`, and the manifest points at that. The original is the record of what the tool actually produced, and a regrid is reproducible from it.
4. Record the tool against every sheet in `art/SOURCES.md`, so both store declarations can be filled in without archaeology.
5. Do not upload anything that would be unwelcome as training data.
6. Replace a category, not a sprite. The four platoons told apart by colour, a ground that tiles, and a HUD drawn from primitives are each internally consistent, and a single frame from a different tool inside any of them reads as a fault rather than as a style.

## What a sheet has to look like

For the importer:

- A uniform grid, every cell the same size. Gutters are measured per sheet rather than assumed.
- A flat key background: green, magenta or white, whichever sits furthest from what is drawn. Edge spill is removed at import, so a soft edge against the key is fine and a mid-green shadow is not.
- Labels under the cells are allowed. `Crop` takes the band off before the cell is trimmed.
- Blank cells are allowed and frames need not be in reading order. `Frames` says how many there are and `Order` says which cells they are.
- A watermark is tolerable on a sprite sheet, where the subject ends up thirty pixels wide and its sparkle is two. It is not tolerable on anything that tiles, where it repeats in a grid and reads as a rendering fault. `Watermark` patches a named rectangle for those.
- The sheets in hand are 1024x1024 and 1456x720. That is a floor, not a target.

For the game:

- A mole is about three quarters of a metre across on a map sixty metres wide, so detail drawn at a realistic scale disappears. Six metres to a ground tile is the compromise, and it was arrived at by rendering frames and looking at them rather than by arithmetic.
- An animation's motion has to be the motion that was drawn, with the subject held in a consistent position across the frames. They are cut with one rectangle for the whole set, so a subject that wanders around its cell wanders on screen too.

## Credits

There is no `LICENSE` and no credits file yet, which `docs/sound-sourcing.md` already flags as worth settling before the first attributed asset lands. Generated art needs no attribution and so does not force that question, but both store declarations do need the tool names, which is what `art/SOURCES.md` is for.
