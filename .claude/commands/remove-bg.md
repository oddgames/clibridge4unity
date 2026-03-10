---
description: Remove the background from an image using AI (rembg). Provide a file path as the argument.
---

Remove the background from the image at: $ARGUMENTS

## Step 1: Detect background type

Use the Read tool to view the image first. Determine the background type:

- **Solid chroma color** (green screen, blue screen, magenta, or any uniform solid color): Use the **chroma key** method below — it's faster and produces cleaner edges.
- **Arbitrary/complex background** (photos, gradients, scenes): Use the **rembg AI** method below.

If in doubt, ask the user which method to use.

## Method A: Chroma Key Removal (for solid-color backgrounds)

Write this Python script to a temp file and run it. Replace INPUT_PATH, OUTPUT_PATH, and CHROMA_RGB with the detected background color as an (R, G, B) tuple.

```python
import numpy as np
from PIL import Image, ImageFilter
from scipy.ndimage import binary_dilation, binary_erosion
import sys

input_path = r'INPUT_PATH'
output_path = r'OUTPUT_PATH'
key = np.array(CHROMA_RGB, dtype=np.float64)

img = Image.open(input_path).convert("RGBA")
arr = np.array(img, dtype=np.float64)
rgb = arr[:, :, :3]

# Identify hot (dominant) and cold channels relative to the key color
hot_channels = key > 128
cold_channels = key < 128
# Handle neutral keys (e.g. gray) — treat brightest as hot, rest as cold
if not np.any(hot_channels) or not np.any(cold_channels):
    hot_channels = key >= np.max(key)
    cold_channels = ~hot_channels
hot_idx = np.where(hot_channels)[0]
cold_idx = np.where(cold_channels)[0]

# Phase 1: Alpha from color distance
diff = rgb - key[np.newaxis, np.newaxis, :]
dist = np.sqrt(np.sum(diff ** 2, axis=2))
inner_thresh, outer_thresh = 50, 180
alpha_norm = np.clip((dist - inner_thresh) / (outer_thresh - inner_thresh), 0.0, 1.0)

# Recover foreground via compositing math
a = alpha_norm[:, :, np.newaxis]
a_safe = np.maximum(a, 0.005)
fg = (rgb - (1.0 - a) * key[np.newaxis, np.newaxis, :]) / a_safe
fg = np.clip(fg, 0.0, 255.0)
fg[alpha_norm < 0.005] = 0.0

# Phase 2: Edge zone mask
fg_mask = alpha_norm > 0.5
bg_region = alpha_norm < 0.01
struct = np.ones((3, 3), dtype=bool)
edge_zone = binary_dilation(bg_region, structure=struct, iterations=4) & fg_mask
semi_transparent = (alpha_norm > 0.005) & (alpha_norm < 0.99)
edge_zone = edge_zone | semi_transparent
safe_interior = fg_mask & ~edge_zone
safe_interior = binary_erosion(safe_interior, structure=struct, iterations=1)
print(f"  Edge zone: {np.count_nonzero(edge_zone):,} px | Interior: {np.count_nonzero(safe_interior):,} px")

# Phase 3: Build reference color map from safe interior (progressive kernels)
from scipy.ndimage import uniform_filter
ref_color = np.zeros_like(fg)
has_ref = np.zeros(fg.shape[:2], dtype=bool)
for ksize in [51, 101, 201, 301]:
    if np.all(has_ref[edge_zone]):
        break
    weight = np.zeros(fg.shape[:2], dtype=np.float64)
    weight[safe_interior & (alpha_norm > 0.9)] = 1.0
    w_sum = uniform_filter(weight, size=ksize)
    for ch in range(3):
        weighted = fg[:, :, ch] * weight
        w_ch = uniform_filter(weighted, size=ksize)
        valid = w_sum > 0.001
        ref_color[:, :, ch][valid & ~has_ref] = (w_ch[valid & ~has_ref] / w_sum[valid & ~has_ref])
    newly_valid = (w_sum > 0.001) & ~has_ref
    has_ref |= newly_valid
    pct = np.count_nonzero(has_ref & edge_zone) / max(np.count_nonzero(edge_zone), 1) * 100
    print(f"  Reference map (kernel {ksize}): {pct:.0f}% edge coverage")

# Phase 4: Edge-only iterative despill
if len(hot_idx) > 0 and len(cold_idx) > 0:
    for iteration in range(5):
        tolerance = max(25 - iteration * 5, 5)
        blend = min(0.7 + iteration * 0.05, 0.95)
        contaminated_count = 0
        for hi in hot_idx:
            edge_hot = fg[:, :, hi]
            ref_hot = ref_color[:, :, hi]
            edge_cold_avg = np.mean(fg[:, :, cold_idx], axis=2)
            contaminated = (
                edge_zone & has_ref &
                (edge_hot > ref_hot + tolerance) &
                (edge_hot > edge_cold_avg + tolerance + 5)
            )
            n = np.count_nonzero(contaminated)
            contaminated_count += n
            if n > 0:
                fg[:, :, hi][contaminated] = (
                    edge_hot[contaminated] * (1 - blend) + ref_hot[contaminated] * blend
                )
        print(f"  Despill pass {iteration+1} (tol={tolerance}, blend={blend:.0%}): {contaminated_count:,} px")
        if contaminated_count == 0:
            break

fg = np.clip(fg, 0.0, 255.0)

# Phase 5: Alpha erosion + feather
alpha_u8 = (alpha_norm * 255).astype(np.uint8)
alpha_img = Image.fromarray(alpha_u8, mode='L')
alpha_img = alpha_img.filter(ImageFilter.MinFilter(3))
alpha_img = alpha_img.filter(ImageFilter.MinFilter(3))
alpha_img = alpha_img.filter(ImageFilter.GaussianBlur(radius=0.7))
final_alpha = np.array(alpha_img, dtype=np.float64)

# Phase 6: Post-erosion cleanup loop
final_semi = (final_alpha > 0) & (final_alpha < 255)
final_semi_ref = final_semi & has_ref
final_semi_noref = final_semi & ~has_ref

for cleanup_pass in range(4):
    # Measure contamination
    semi_mask = final_alpha > 0
    semi_mask &= final_alpha < 255
    if not np.any(semi_mask):
        break
    hot_avg = np.mean(fg[:, :, hot_idx], axis=2)
    cold_avg = np.mean(fg[:, :, cold_idx], axis=2)
    spill = hot_avg - cold_avg
    n_contam = np.count_nonzero(semi_mask & (spill > 3))
    max_spill = np.max(spill[semi_mask]) if np.any(semi_mask) else 0

    if n_contam == 0 and max_spill <= 5:
        print(f"  Cleanup pass {cleanup_pass+1}: CLEAN (max spill {max_spill:.1f})")
        break

    print(f"  Cleanup pass {cleanup_pass+1}: {n_contam:,} contaminated, max spill={max_spill:.1f}")
    fa_norm = final_alpha / 255.0

    # Reference-based recolor
    if np.count_nonzero(final_semi_ref) > 0:
        alpha_ceiling = max(0.8 - cleanup_pass * 0.15, 0.3)
        bs = np.clip((alpha_ceiling - fa_norm) / 0.5, 0.0, 1.0)
        for ch in range(3):
            fg_ch = fg[:, :, ch]
            ref_ch = ref_color[:, :, ch]
            fg_ch[final_semi_ref] = fg_ch[final_semi_ref] * (1 - bs[final_semi_ref]) + ref_ch[final_semi_ref] * bs[final_semi_ref]

        margin = max(15 - cleanup_pass * 4, 3)
        c_avg = np.mean(fg[:, :, cold_idx], axis=2)
        for hi in hot_idx:
            fg_hi = fg[:, :, hi]
            ref_hi = ref_color[:, :, hi]
            contaminated = final_semi_ref & ((fg_hi - ref_hi) > margin) & ((fg_hi - c_avg - margin) > 0)
            if np.count_nonzero(contaminated) > 0:
                b = min(0.7 + cleanup_pass * 0.1, 0.95)
                fg_hi[contaminated] = fg_hi[contaminated] * (1 - b) + ref_hi[contaminated] * b

    # No-reference fallback
    if np.count_nonzero(final_semi_noref) > 0:
        c_avg = np.mean(fg[:, :, cold_idx], axis=2)
        margin = max(10 - cleanup_pass * 3, 0)
        for hi in hot_idx:
            fg_hi = fg[:, :, hi]
            excess = fg_hi - (c_avg + margin)
            needs_clamp = final_semi_noref & (excess > 0)
            if np.count_nonzero(needs_clamp) > 0:
                fg_hi[needs_clamp] = c_avg[needs_clamp] + margin

    # Universal spill neutralizer
    c_val = np.mean(fg[:, :, cold_idx], axis=2)
    h_avg = np.mean(fg[:, :, hot_idx], axis=2)
    sp = h_avg - c_val
    sp_thresh = max(3 - cleanup_pass, 0)
    needs_fix = final_semi & (sp > sp_thresh)
    if np.count_nonzero(needs_fix) > 0:
        aggression = min(0.6 + cleanup_pass * 0.15, 1.0)
        alpha_factor = 1.0 - (1.0 - aggression) * (final_alpha / 255.0)
        removal = sp * alpha_factor
        for hi in hot_idx:
            fg_hi = fg[:, :, hi]
            fg_hi[needs_fix] = np.maximum(fg_hi[needs_fix] - removal[needs_fix], 0.0)

    fg = np.clip(fg, 0.0, 255.0)

# Assemble final RGBA
result_arr = np.zeros((*fg.shape[:2], 4), dtype=np.uint8)
result_arr[:, :, :3] = np.clip(fg, 0, 255).astype(np.uint8)
result_arr[:, :, 3] = np.array(alpha_img, dtype=np.uint8)

result = Image.fromarray(result_arr, mode='RGBA')
result.save(output_path)
print(f"Saved: {output_path}")
```

To detect the chroma color: sample a few corner pixels from the image. If they're all within ~10 RGB distance of each other, that's the background color.

## Method B: AI Removal (rembg, for complex backgrounds)

```bash
python -c "
from rembg import remove
from PIL import Image
inp = Image.open(r'INPUT_PATH')
out = remove(inp)
out.save(r'OUTPUT_PATH')
print('Background removed successfully')
"
```

If rembg is not installed, run `pip install rembg pillow` first.

## Rules
- Replace INPUT_PATH with the provided file path
- By default, save OUTPUT_PATH to the same path (overwrite the original)
- If the user specifies a different output path, use that instead
- For chroma method: auto-detect the background color from corner pixels, or ask the user
- For chroma method: ensure `scipy` is installed (`pip install scipy pillow numpy`)
- Use a 120-second timeout
- After saving, use the Read tool to show the result to the user
