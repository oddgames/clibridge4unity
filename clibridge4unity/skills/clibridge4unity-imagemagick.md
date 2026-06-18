---
name: clibridge4unity-imagemagick
description: Install and use ImageMagick to edit images from the command line — resize, crop, convert formats (png/jpg/webp/gif/tiff/heic/svg/pdf), compress, rotate/flip, annotate text, composite/overlay, trim, recolor, build montages/contact-sheets, and read metadata. Use whenever you need to edit, convert, optimize, batch-process, or inspect an image file without opening an editor — e.g. resizing a SCREENSHOT, making an icon, or prepping a texture. Auto-installs ImageMagick if the `magick` command is missing.
---

# ImageMagick

Command-line image editing. ImageMagick 7 ships a single `magick` driver (legacy
`convert`/`mogrify`/`identify`/`composite` still work). Pairs well with `clibridge4unity SCREENSHOT`
for cropping/annotating/resizing the PNGs it drops in `%TEMP%/clibridge4unity_screenshots/`.

## 0. Ensure it's installed (check once)

```bash
magick -version          # prints "Version: ImageMagick 7.x ..." if present
```

If missing, install for the platform, then re-check:

- **Windows**: `winget install --id ImageMagick.ImageMagick -e`  (or `choco install imagemagick -y`) — may need a fresh shell for PATH.
- **macOS**: `brew install imagemagick`
- **Linux**: `sudo apt-get install -y imagemagick`  /  `sudo dnf install -y ImageMagick`

HEIC/AVIF/RAW need `libheif`; SVG needs `librsvg`; PDF/PS need `ghostscript`. Install those only
if a conversion fails with a "no decode delegate" error.

## 1. Inspect first

```bash
magick identify in.png                              # format, WxH, depth, colorspace
magick identify -format '%wx%h %m\n' in.png         # scriptable
magick identify -verbose in.png | head -40          # EXIF/profiles
```

## 2. Core edits

```bash
# Resize ('>' shrinks only; '^'+extent fills then crops to exact)
magick in.jpg -resize 800x600 out.jpg               # fit within box, keep aspect
magick in.jpg -resize 50% out.jpg
magick in.jpg -resize '1024x1024>' out.jpg          # cap longest side at 1024
magick in.jpg -resize 800x600^ -gravity center -extent 800x600 out.jpg

# Convert format (extension picks the codec)
magick in.png out.webp
magick in.heic out.jpg
magick in.svg -density 300 -background none out.png # rasterize vector
magick in.pdf[0] -density 200 page0.png             # first PDF page

# Compress / strip metadata
magick in.jpg -quality 82 -strip out.jpg
magick in.png -strip -define png:compression-level=9 out.png
magick in.png -quality 80 -define webp:method=6 out.webp

# Crop / trim / pad  (+repage resets the canvas offset — always after crop/trim)
magick in.png -crop 400x300+50+20 +repage out.png   # WxH+xoff+yoff
magick in.png -trim +repage out.png                 # auto-remove uniform border
magick in.png -gravity center -background white -extent 1000x1000 out.png
magick in.png -bordercolor '#222' -border 20 out.png

# Rotate / flip / orient
magick in.jpg -rotate 90 out.jpg
magick in.jpg -flop out.jpg                          # -flip = vertical
magick in.jpg -auto-orient out.jpg                   # apply then clear EXIF rotation

# Color / tone / transparency
magick in.jpg -colorspace Gray out.jpg
magick in.jpg -modulate 100,120,100 out.jpg          # brightness,saturation,hue
magick in.png -fuzz 8% -transparent white out.png    # near-white → transparent
magick in.png -background '#0d1117' -flatten out.jpg # flatten alpha onto solid bg
```

## 3. Composite, text, batch

```bash
# Overlay (logo bottom-right with 20px margin)
magick base.png logo.png -gravity southeast -geometry +20+20 -composite out.png

# Caption / make a card from scratch
magick in.png -gravity south -pointsize 36 -fill white -stroke black -strokewidth 1 \
  -annotate +0+20 'Caption' out.png
magick -size 1200x630 xc:'#0d1117' -gravity center -fill white -pointsize 64 \
  -annotate 0 'Title' card.png

# Batch (mogrify edits in place — point -path at an output dir to keep originals)
magick mogrify -resize '1280x1280>' -path out_dir/ *.jpg

# Contact sheet / GIF
magick montage *.png -tile 4x -geometry +6+6 -background '#111' contact.png
magick -delay 8 -loop 0 frame_*.png anim.gif
magick anim.gif -coalesce frames/%03d.png            # explode GIF to frames
```

PowerShell batch loop:

```powershell
Get-ChildItem *.png | ForEach-Object { magick $_.FullName -strip -quality 82 "out/$($_.BaseName).jpg" }
```

## Pitfalls

- **Option order matters**: read-time options (`-density`, `-background` for SVG/PDF) go *before* the input; edit options go between input and output. Build the command left-to-right as a pipeline.
- **`+repage` after every crop/trim**, or the leftover canvas offset misplaces later ops.
- **Never overwrite the source in one step** unless intended — write a new file, then `magick identify out.ext` to confirm before replacing.
- **`-strip`** drops EXIF/ICC (good for web) but also orientation — run `-auto-orient` first.
- **"operation not allowed by the security policy"** = a restrictive `/etc/ImageMagick-*/policy.xml` blocking PDF/PS; edit that file or use a different delegate.
