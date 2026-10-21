# Offering Radiance looks from your mod

If your mod or collection wants to come with a Radiance look, please don't write over Radiance's
`config.json`. That throws away the player's own settings and saved looks, and the next update of
either mod can undo the other.

Ship a `radiance-looks.json` instead. Radiance lists the looks in it in F6, in the Looks tab under
**Looks from your mods**. Nothing changes until the player picks one, and picking one works the
same as pasting a share code: they see what will change, then choose Keep or Put mine back.

## The file

Put `radiance-looks.json` in your mod's folder, next to its `manifest.json` (a subfolder of it
works too):

```json
{
  "looks": [
    {
      "name": "Golden autumn",
      "description": "Warm light, soft bloom, long evening shadows.",
      "code": "RAD1L-..."
    }
  ]
}
```

- `name`: what the button says. 40 characters at most.
- `description`: optional, shown when the player hovers the button.
- `code`: a share code. Set the look up in F6, then use **Copy look** (a code starting `RAD1L`) or
  **Copy everything** (`RAD1F`) and paste it here.

Up to 12 looks per mod. Comments and trailing commas are fine. The mod's name from its manifest is
shown under each look, so players know where it came from.

## How a code behaves on other versions

A share code only carries settings, so it keeps working as Radiance changes:

- A setting your code has that the player's Radiance doesn't know yet is skipped.
- A setting their Radiance knows that your code leaves out goes back to its default.

## Checking it

Run `radiance_looks` in the SMAPI console. It reads the files again and lists every look it found.
If a look was left out, the console says why: no name, a name that is too long, or a code this
version cannot read.
