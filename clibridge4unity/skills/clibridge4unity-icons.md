---
name: clibridge4unity-icons
description: Use for any UI icon work — finding an icon, importing it into a Unity project, displaying it in UI Toolkit or uGUI, theming/tinting it from USS or code. Auto-trigger on react-icons, lucide, Font Awesome, Material Design icons, Heroicons, Tabler, Phosphor, "I need an icon for …", `Sprite`, `Image.sprite`, `-unity-background-image`, `-unity-background-image-tint-color`, `.svg`, `magick`, `inkscape`, "the icon is black", "tint doesn't work", "icon is pixelated", asset name conventions, icon folder organisation. Picking one source + one import pipeline + one tint mechanism = consistent icons across every screen.
---

# Unity Icons

Standard UI Toolkit / uGUI icon facts (white source + `-unity-background-image-tint-color`, `resource()` loading, import settings, SVG `currentColor`) are general knowledge — apply them normally. Below is only this project's standardised pipeline and conventions.

## House conventions

**One source, one import pipeline, one tint mechanism.**

- **Catalogue:** [react-icons.github.io](https://react-icons.github.io) for discovery (30+ libraries); grab the raw SVG from the upstream repo for production.
- **PRIMARY library:** Lucide (default). Material Design Icons if you need ~7000; Heroicons if Tailwind-aligned. Secondary libraries only when the primary lacks the icon. Document the choice in CLAUDE.md.
- **Naming:** `Assets/Resources/UI/Icons/<library-prefix>_<icon-name>.png` — `lu_` Lucide, `fa_` Font Awesome, `mdi_` Material (e.g. `lu_settings.png`). Prefix makes the source library visible at the call site.
- Edit the SVG to white (`#FFFFFF`) in a workspace folder *outside* the Unity project; keep that as the source.

## Render command (white PNG at 4× display size)

```bash
magick -background none -density 600 \
  ./workspace/lu_settings-white.svg -resize 128x128 \
  ./Assets/Resources/UI/Icons/lu_settings.png
```

- `-density 600` for SVG rasterisation; `-resize 128x128` = 4× the typical 32×32 display size.
- Inkscape alt: `inkscape icon-white.svg --export-type=png --export-filename=lu_settings.png --export-background-opacity=0 --export-width=128`.

## Import enforcement

Enforce icon import settings via AssetPostprocessor gated on path `"/Resources/UI/Icons/"` (Sprite type, Compression None, Bilinear, mipmaps off, alphaIsTransparency on). See `clibridge4unity-editor-tools` for the pattern.

## Verify from the bridge

After rendering + importing, confirm the icon landed and reads correctly without opening Unity:

```bash
clibridge4unity ASSET_DISCOVER sprites                 # confirm the icon imported under the sprites category
clibridge4unity REIMPORT Assets/Resources/UI/Icons/lu_settings.png   # re-apply the AssetPostprocessor import settings
clibridge4unity SCREENSHOT Assets/UI/Toolbar.uxml --el "#settings-btn"  # see the tint applied in context
clibridge4unity LOG ui errors                          # USS tint / -unity-background-image import failures
# Read the real import settings when "icon is black" / "tint doesn't work":
clibridge4unity CODE_EXEC_RETURN "(AssetImporter.GetAtPath(\"Assets/Resources/UI/Icons/lu_settings.png\") as TextureImporter)?.textureType"
```

"Icon is black" / "tint doesn't work" almost always means the source PNG wasn't white, or wasn't imported as a Sprite (`textureType` returns `Default`, not `Sprite`). `SCREENSHOT` the UXML to see the actual tinted result instead of guessing.

## Related
- `clibridge4unity-ui-toolkit` — USS background-image tinting
- `clibridge4unity-editor-tools` — AssetPostprocessor import settings; icons in editor windows
