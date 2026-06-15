---
name: clibridge4unity-icons
description: Use for any UI icon work — finding an icon, importing it into a Unity project, displaying it in UI Toolkit or uGUI, theming/tinting it from USS or code. Auto-trigger on react-icons, lucide, Font Awesome, Material Design icons, Heroicons, Tabler, Phosphor, "I need an icon for …", `Sprite`, `Image.sprite`, `-unity-background-image`, `-unity-background-image-tint-color`, `.svg`, `magick`, `inkscape`, "the icon is black", "tint doesn't work", "icon is pixelated", asset name conventions, icon folder organisation. Picking one source + one import pipeline + one tint mechanism = consistent icons across every screen.
---

# Unity Icons

The right icon pipeline is invisible: a designer or engineer says "I need a settings icon" and the file is in the project, in the right format, ready to be tinted by USS. The wrong pipeline produces black icons in production, pixelated icons on high-DPI screens, or N "settings" icons in 4 different art styles because every developer picked a different source. This skill is the standardised pipeline.

## The one rule that prevents most of these

**One source, one import pipeline, one tint mechanism.** Source: react-icons.github.io as the catalogue, upstream icon repos as the raw SVG. Pipeline: render SVG → white PNG at 4× target display size via ImageMagick. Tint: USS `-unity-background-image-tint-color`. Never bake colour into the PNG.

## The pipeline

### 1. Pick from react-icons.github.io as the catalogue

[react-icons.github.io](https://react-icons.github.io) is an aggregated index of 30+ icon libraries (Lucide, Font Awesome, Material Design, Heroicons, Tabler, Phosphor, Bootstrap, Iconoir, Octicons, …). It's the discovery tool — for production you grab the raw SVG from the upstream repo (cleaner, no React wrapping).

- **Library choice:** prefer libraries that have a *family* of icons rendered in the same style (Lucide, Heroicons, Material Design). Don't mix Lucide's stroke-only style with Material's filled style in the same screen.
- **Naming:** `Assets/Resources/UI/Icons/<library-prefix>_<icon-name>.png` (e.g. `lu_settings.png` for Lucide, `fa_trash.png` for Font Awesome, `mdi_account.png` for Material). The prefix makes the source library visible at the call site; consistent across the project.

### 2. Render SVG → white PNG at 4× target display size

The trick: render the icon as white (`#FFFFFF`) on transparent at 4× the size you'll display, then tint via USS. White at high resolution + USS tint gives you free theming, free dark/light mode, and crisp rendering on every DPI.

**ImageMagick (Windows/Mac/Linux):**
```bash
magick -background none -density 600 \
  icon-white.svg -resize 128x128 \
  Assets/Resources/UI/Icons/lu_settings.png
```

Replace `icon-white.svg` with the source SVG. Edit the SVG first to replace `currentColor` / `stroke="..."` / `fill="..."` with `#FFFFFF` — most upstream libraries use `currentColor` to inherit text colour, which renders as transparent black if you skip the substitution.

- `-density 600` is the DPI used for SVG rasterisation. Lower → anti-aliasing mush; higher → over-sharp.
- `-resize 128x128` is 4× the typical 32×32 display size (handles high-DPI screens cleanly).
- `-background none` preserves transparency.

**Inkscape (alternative):**
```bash
inkscape icon-white.svg --export-type=png --export-filename=lu_settings.png \
  --export-background-opacity=0 --export-width=128
```

### 3. Tint via USS (never bake colour)

Once the white PNG is in `Resources/UI/Icons/`, use it as a `background-image` in USS and tint:

```css
.icon {
    width: 24px;
    height: 24px;
    -unity-background-image-tint-color: var(--text-color);
    background-image: resource("UI/Icons/lu_settings");
}

.icon.danger {
    -unity-background-image-tint-color: rgb(220, 60, 60);
}
```

- `resource("UI/Icons/lu_settings")` loads from `Assets/Resources/UI/Icons/lu_settings.png`.
- `-unity-background-image-tint-color` multiplies the background image's colour — works correctly only if the source PNG is white (otherwise the tint is multiplied by the baked colour and you get dim weirdness).
- Theme switching = swap CSS variables, every icon retints for free.

For uGUI (legacy Image component):
```csharp
image.sprite  = Resources.Load<Sprite>("UI/Icons/lu_settings");
image.color   = themeColor;       // works the same way — multiplies the white image
```

## Pitfall catalog

### 1. The icon renders as transparent black
Cause: the upstream SVG uses `currentColor` or a non-white fill, and you didn't substitute it with `#FFFFFF` before rendering.
- **Rule:** before rendering, open the SVG and grep for `currentColor`, `stroke="#...`, `fill="#...`. Replace all colour values with `#FFFFFF`. The tint mechanism multiplies — white × tint = tint; black × tint = black.

### 2. The icon is pixelated on high-DPI screens
Cause: rendered at 1× target size, displayed at 1× or stretched. Retina / 4K screens upsample = pixelation.
- **Rule:** render at 4× target display size. A 24×24 displayed icon → 96×96 PNG (or 128×128 for headroom). Don't worry about file size; PNG compression handles white-on-transparent very well.

### 3. The icon's anti-aliasing looks fuzzy
Cause: `-density` too low (default is often 72-96 DPI). The SVG rasteriser sees too few pixels of detail and blurs.
- **Rule:** `-density 600` or higher for ImageMagick. Inkscape: `--export-width=` larger than target, then resize. Reduce after, not at, the rasteriser step.

### 4. Tinting locks to one colour because the PNG isn't white
Cause: the PNG has a baked colour (someone exported from Figma with the design's stroke colour intact). USS tint multiplies → the only way to display it at the original colour is `tint-color: white`, and you can't recolour.
- **Rule:** always re-export from a white SVG. Don't trust artist-supplied PNGs as-is; rerender from the SVG into your pipeline.

### 5. Mixed icon styles in the same screen
Cause: every developer picks their favourite library. Now the navigation bar has Lucide stroke icons next to Font Awesome filled icons next to a custom illustrator export.
- **Rule:** project picks one PRIMARY library (Lucide is a common modern choice — open MIT, consistent stroke style, ~1500 icons). Other libraries are secondary, used only when the primary doesn't have what you need. Document the choice in the project's CLAUDE.md / readme.

### 6. Icons in `Assets/` but not under `Resources/` → not loadable by name
`Resources.Load<T>(path)` and USS `resource("...")` only look under `Assets/.../Resources/`. An icon at `Assets/Art/Icons/foo.png` is not loadable by name.
- **Rule:** Resources is fine for icons (small, ubiquitous, loaded at startup). Don't shove gameplay assets into Resources — that's what Addressables is for. Icons fit Resources's intent perfectly. Keep them at `Assets/Resources/UI/Icons/`.

### 7. Icons under per-platform texture overrides get compressed → fuzzy on device
Default texture compression (ASTC/BC) interpolates across blocks, which mushes icon edges.
- **Rule:** all icon imports use `Sprite (2D and UI)` texture type, `Compression: None` (or `Low`), `Filter Mode: Bilinear`, mipmaps off. An AssetPostprocessor can enforce this for files under `Resources/UI/Icons/`. See the `clibridge4unity-shaders-gpu` skill for the AssetPostprocessor pattern.

### 8. Icons inside a UI Builder USS preview render correctly, in play mode they don't
Cause: the USS sheet isn't loaded by the runtime panel (UI Builder loaded it for preview, but your `UIDocument`'s panel settings or runtime `styleSheets` list doesn't include it). USS rules silently don't apply.
- **Rule:** verify the icon's USS rule is in a sheet that's imported via `<Style src="..."/>` in the UXML. The UXML is the source of truth.

## Workflow

1. **Pick the library** for this project. Document it. Lucide is the modern default; Material Design Icons if you need ~7000 icons; Heroicons if your design system is Tailwind-aligned.
2. **For each new icon**, browse react-icons.github.io, find it, click through to the upstream repo to get the raw SVG.
3. **Edit the SVG**: substitute every colour reference with `#FFFFFF`. Save as `<library-prefix>_<icon-name>.svg` in a workspace folder (not in the Unity project; that folder is for the source).
4. **Render**: `magick -background none -density 600 source.svg -resize 128x128 Assets/Resources/UI/Icons/<prefix>_<name>.png`.
5. **Reference in USS**: `background-image: resource("UI/Icons/<prefix>_<name>");` + `-unity-background-image-tint-color: var(--icon-tint);`.
6. **Verify in both light and dark themes** by swapping `--icon-tint` between two contrasting colours. If the icon looks right in both, your pipeline is correct.

## Quick reference — full re-render of one icon

```bash
# 1. Save the upstream SVG (e.g. from https://github.com/lucide-icons/lucide/raw/main/icons/settings.svg)
# 2. Edit: substitute every "currentColor" / "#000" / "stroke=..." with "#FFFFFF"
# 3. Render:
magick -background none -density 600 \
  ./workspace/lu_settings-white.svg \
  -resize 128x128 \
  ./Assets/Resources/UI/Icons/lu_settings.png
```

## Quick reference — USS theme tokens for icons

```css
:root {
    --icon-color-primary:   rgb(220, 220, 220);
    --icon-color-secondary: rgb(150, 150, 150);
    --icon-color-danger:    rgb(220, 60, 60);
    --icon-color-success:   rgb(60, 220, 100);
}

.icon {
    width: 24px;
    height: 24px;
    -unity-background-image-tint-color: var(--icon-color-primary);
    background-size: contain;
}

.icon.danger  { -unity-background-image-tint-color: var(--icon-color-danger); }
.icon.success { -unity-background-image-tint-color: var(--icon-color-success); }

.icon-settings  { background-image: resource("UI/Icons/lu_settings"); }
.icon-trash     { background-image: resource("UI/Icons/fa_trash"); }
.icon-account   { background-image: resource("UI/Icons/mdi_account"); }
```

## Quick reference — AssetPostprocessor to enforce icon import settings

```csharp
class IconImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/Resources/UI/Icons/")) return;
        if (!assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;

        var ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.mipmapEnabled = false;
        ti.filterMode = FilterMode.Bilinear;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.alphaIsTransparency = true;
    }
}
```

## Related
- `clibridge4unity-ui-toolkit` — USS `background-image` + `-unity-background-image-tint-color` for theming
- `clibridge4unity-shaders-gpu` — `AssetPostprocessor` pattern for enforcing per-asset import settings
- `clibridge4unity-editor-tools` — using icons in editor windows (USS `resource()` works in editor UI too)
