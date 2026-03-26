#!/usr/bin/env python
"""Analyze a UI reference image: OCR, structure, colors, fonts, icons, spacing, gradients + Gemini review."""
import sys, os, json, base64, urllib.request, urllib.error, time, glob


# =============================================================================
# 1. COLOR PALETTE EXTRACTION (KMeans)
# =============================================================================
def extract_color_palette(image_path, n_colors=8):
    """Extract dominant colors using KMeans clustering."""
    import cv2
    import numpy as np
    from sklearn.cluster import KMeans

    img = cv2.imread(image_path)
    img_rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
    h, w = img.shape[:2]

    # Downsample for speed
    scale = max(1, int((h * w / 100000) ** 0.5))
    small = img_rgb[::scale, ::scale].reshape(-1, 3).astype(np.float32)

    km = KMeans(n_clusters=n_colors, n_init=3, max_iter=100, random_state=42)
    km.fit(small)

    centers = km.cluster_centers_.astype(int)
    labels = km.labels_
    counts = np.bincount(labels)
    total = len(labels)

    palette = []
    for idx in np.argsort(-counts):
        c = centers[idx]
        pct = counts[idx] / total * 100
        hex_color = f"#{c[0]:02x}{c[1]:02x}{c[2]:02x}"
        palette.append({
            "hex": hex_color,
            "rgb": f"rgb({c[0]},{c[1]},{c[2]})",
            "percent": round(pct, 1),
        })

    return palette


# =============================================================================
# 2. SPACING / GRID ANALYSIS (scipy)
# =============================================================================
def analyze_spacing(text_elements, rows, image_size):
    """Detect consistent spacing patterns: row heights, column positions, padding."""
    import numpy as np
    from scipy.signal import find_peaks

    w, h = image_size["width"], image_size["height"]
    result = {"row_heights": [], "column_positions": [], "padding": {}, "consistent_row_height": None}

    if len(rows) < 2:
        return result

    # Row Y positions and heights
    row_ys = []
    for row in rows:
        row_y = min(e["bounds"]["y"] for e in row)
        row_h = max(e["bounds"]["y"] + e["bounds"]["height"] for e in row) - row_y
        row_ys.append(row_y)
        result["row_heights"].append(row_h)

    # Gaps between consecutive rows
    gaps = [row_ys[i+1] - row_ys[i] for i in range(len(row_ys)-1)]
    if gaps:
        gaps_arr = np.array(gaps)
        median_gap = float(np.median(gaps_arr))
        std_gap = float(np.std(gaps_arr))
        result["consistent_row_height"] = {
            "median_gap": round(median_gap, 1),
            "std_dev": round(std_gap, 1),
            "is_uniform": std_gap < median_gap * 0.15,
            "gaps": [round(g, 1) for g in gaps],
        }

    # Column positions — find X clusters
    all_x = [e["bounds"]["x"] for e in text_elements]
    if all_x:
        x_arr = np.array(sorted(set(all_x)))
        # Find clusters of X positions (columns)
        if len(x_arr) > 1:
            diffs = np.diff(x_arr)
            # Big jumps indicate column boundaries
            big_jumps = np.where(diffs > w * 0.1)[0]
            columns = [int(x_arr[0])]
            for j in big_jumps:
                columns.append(int(x_arr[j + 1]))
            result["column_positions"] = columns

    # Padding — distance from panel edges to content
    if text_elements:
        min_x = min(e["bounds"]["x"] for e in text_elements)
        max_x = max(e["bounds"]["x"] + e["bounds"]["width"] for e in text_elements)
        min_y = min(e["bounds"]["y"] for e in text_elements)
        max_y = max(e["bounds"]["y"] + e["bounds"]["height"] for e in text_elements)
        result["padding"] = {
            "left": min_x,
            "right": w - max_x,
            "top": min_y,
            "bottom": h - max_y,
        }

    return result


# =============================================================================
# 3. GRADIENT DETECTION
# =============================================================================
def detect_gradients(image_path, panels):
    """Detect if panel backgrounds use gradients vs solid colors."""
    import cv2
    import numpy as np

    img = cv2.imread(image_path)
    h, w = img.shape[:2]
    gradients = []

    regions = []
    if panels:
        for p in panels[:5]:  # Check top 5 panels
            b = p["bounds"]
            regions.append(("panel", b))
    else:
        # Check full image in quadrants
        regions.append(("full", {"x": 0, "y": 0, "width": w, "height": h}))

    for label, b in regions:
        x, y, rw, rh = b["x"], b["y"], b["width"], b["height"]
        # Inset slightly to avoid borders
        margin = 10
        roi = img[y+margin:y+rh-margin, x+margin:x+rw-margin]
        if roi.size == 0:
            continue

        # Sample vertical strip (center column)
        cx = roi.shape[1] // 2
        v_strip = roi[:, max(0, cx-5):cx+5, :].mean(axis=1).astype(float)

        # Sample horizontal strip (center row)
        cy = roi.shape[0] // 2
        h_strip = roi[max(0, cy-5):cy+5, :, :].mean(axis=0).astype(float)

        # Check variance along each axis
        v_var = float(np.std(v_strip, axis=0).mean())
        h_var = float(np.std(h_strip, axis=0).mean())

        # Determine gradient direction
        is_gradient = v_var > 8 or h_var > 8
        if is_gradient:
            if v_var > h_var * 1.5:
                direction = "vertical"
                # Sample top and bottom colors
                top_color = v_strip[:5].mean(axis=0).astype(int)
                bot_color = v_strip[-5:].mean(axis=0).astype(int)
                color_from = f"rgb({top_color[2]},{top_color[1]},{top_color[0]})"
                color_to = f"rgb({bot_color[2]},{bot_color[1]},{bot_color[0]})"
            elif h_var > v_var * 1.5:
                direction = "horizontal"
                left_color = h_strip[:5].mean(axis=0).astype(int)
                right_color = h_strip[-5:].mean(axis=0).astype(int)
                color_from = f"rgb({left_color[2]},{left_color[1]},{left_color[0]})"
                color_to = f"rgb({right_color[2]},{right_color[1]},{right_color[0]})"
            else:
                direction = "diagonal"
                tl = roi[:10, :10].mean(axis=(0, 1)).astype(int)
                br = roi[-10:, -10:].mean(axis=(0, 1)).astype(int)
                color_from = f"rgb({tl[2]},{tl[1]},{tl[0]})"
                color_to = f"rgb({br[2]},{br[1]},{br[0]})"

            gradients.append({
                "region": label,
                "bounds": b,
                "direction": direction,
                "color_from": color_from,
                "color_to": color_to,
                "variance_v": round(v_var, 1),
                "variance_h": round(h_var, 1),
            })
        else:
            avg = roi.mean(axis=(0, 1)).astype(int)
            gradients.append({
                "region": label,
                "bounds": b,
                "direction": "solid",
                "color": f"rgb({avg[2]},{avg[1]},{avg[0]})",
                "variance_v": round(v_var, 1),
                "variance_h": round(h_var, 1),
            })

    return gradients


# =============================================================================
# 4. FONT MATCHING (PIL + fontTools)
# =============================================================================
def match_fonts(image_path, text_elements, max_candidates=15):
    """Match OCR text crops against rendered system fonts to find closest match."""
    import cv2
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont
    from fontTools.ttLib import TTFont

    img = cv2.imread(image_path)
    if img is None or not text_elements:
        return {"best_match": "unknown", "candidates": []}

    # Collect system fonts
    font_dir = "C:/Windows/Fonts"
    font_files = glob.glob(os.path.join(font_dir, "*.ttf")) + glob.glob(os.path.join(font_dir, "*.otf"))

    # Build font name -> path mapping (skip symbol/wingding fonts)
    fonts = {}
    for fp in font_files:
        basename = os.path.basename(fp).lower()
        if any(skip in basename for skip in ["wingding", "webding", "symbol", "marlett", "segmdl", "holomdl"]):
            continue
        try:
            tt = TTFont(fp)
            name_table = tt["name"]
            family = None
            style = "Regular"
            for record in name_table.names:
                if record.nameID == 1:  # Font Family
                    try:
                        family = record.toUnicode()
                    except:
                        pass
                elif record.nameID == 2:  # Font Subfamily
                    try:
                        style = record.toUnicode()
                    except:
                        pass
            tt.close()
            if family:
                display_name = f"{family} {style}".strip()
                fonts[display_name] = fp
        except:
            continue

    if not fonts:
        return {"best_match": "unknown", "candidates": []}

    # Pick a few good OCR samples (high confidence, uppercase, reasonable size)
    samples = []
    for e in text_elements:
        if e["confidence"] > 0.8 and e["bounds"]["height"] > 15 and len(e["text"]) >= 3:
            b = e["bounds"]
            crop = img[b["y"]:b["y"]+b["height"], b["x"]:b["x"]+b["width"]]
            if crop.size > 0:
                # Convert to grayscale and threshold
                gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
                # Determine if light text on dark bg or vice versa
                mean_val = gray.mean()
                if mean_val < 128:
                    # Dark background, light text — invert so text is dark on white
                    gray = 255 - gray
                _, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
                samples.append({"text": e["text"], "crop": binary, "height": b["height"]})
        if len(samples) >= 5:
            break

    if not samples:
        return {"best_match": "unknown", "candidates": []}

    # Score each font by rendering the same text and comparing
    scores = {}
    font_list = list(fonts.items())[:max_candidates * 4]  # Check a reasonable number

    for font_name, font_path in font_list:
        total_score = 0
        matched = 0
        for sample in samples:
            try:
                font_size = sample["height"]
                pil_font = ImageFont.truetype(font_path, font_size)

                # Render text
                text = sample["text"]
                # Get text bbox
                dummy = Image.new("L", (sample["crop"].shape[1] * 2, sample["crop"].shape[0] * 2), 255)
                draw = ImageDraw.Draw(dummy)
                bbox = draw.textbbox((0, 0), text, font=pil_font)
                tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
                if tw < 1 or th < 1:
                    continue

                # Render to matching size
                render_img = Image.new("L", (tw + 10, th + 10), 255)
                draw = ImageDraw.Draw(render_img)
                draw.text((5, 5), text, fill=0, font=pil_font)
                rendered = np.array(render_img)

                # Resize both to same dimensions for comparison
                target_h, target_w = sample["crop"].shape[:2]
                if target_h < 5 or target_w < 5:
                    continue
                rendered_resized = cv2.resize(rendered, (target_w, target_h))

                # Threshold rendered
                _, rendered_bin = cv2.threshold(rendered_resized, 128, 255, cv2.THRESH_BINARY)

                # Compare using normalized cross-correlation
                crop_f = sample["crop"].astype(np.float32) / 255.0
                rend_f = rendered_bin.astype(np.float32) / 255.0

                # Structural similarity via correlation
                correlation = np.corrcoef(crop_f.flatten(), rend_f.flatten())[0, 1]
                if not np.isnan(correlation):
                    total_score += correlation
                    matched += 1
            except:
                continue

        if matched > 0:
            scores[font_name] = total_score / matched

    # Sort by score
    ranked = sorted(scores.items(), key=lambda x: x[1], reverse=True)[:max_candidates]

    # Classify font characteristics from best match
    characteristics = {}
    if samples:
        sample = samples[0]
        crop = sample["crop"]
        h_crop, w_crop = crop.shape
        # Aspect ratio — condensed fonts have higher h/w per character
        chars = len(sample["text"])
        char_width = w_crop / max(chars, 1)
        aspect = h_crop / max(char_width, 1)
        if aspect > 2.0:
            characteristics["width"] = "condensed"
        elif aspect > 1.3:
            characteristics["width"] = "normal"
        else:
            characteristics["width"] = "wide"

        # Stroke weight — ratio of dark pixels
        dark_ratio = (crop < 128).sum() / crop.size
        if dark_ratio > 0.45:
            characteristics["weight"] = "bold"
        elif dark_ratio > 0.30:
            characteristics["weight"] = "medium"
        else:
            characteristics["weight"] = "light/regular"

        # Serif detection — check for small protrusions at baselines
        # Simple heuristic: count horizontal edge transitions at top/bottom rows
        top_row = crop[1, :]
        bot_row = crop[-2, :]
        top_transitions = np.sum(np.abs(np.diff(top_row.astype(int))) > 128)
        bot_transitions = np.sum(np.abs(np.diff(bot_row.astype(int))) > 128)
        avg_transitions = (top_transitions + bot_transitions) / 2
        characteristics["serif"] = "serif" if avg_transitions > chars * 3 else "sans-serif"

    return {
        "best_match": ranked[0][0] if ranked else "unknown",
        "best_score": round(ranked[0][1], 3) if ranked else 0,
        "characteristics": characteristics,
        "candidates": [{"font": name, "score": round(score, 3)} for name, score in ranked],
    }


# =============================================================================
# 5. YOLO ICON / ELEMENT DETECTION
# =============================================================================
def detect_icons(image_path, text_elements):
    """Use YOLOv8 to detect non-text UI elements (icons, buttons, arrows)."""
    import cv2
    import numpy as np

    try:
        from ultralytics import YOLO
    except ImportError:
        return []

    # Suppress ultralytics output
    import logging
    logging.getLogger("ultralytics").setLevel(logging.ERROR)

    model = YOLO("yolov8n.pt")
    results = model(image_path, verbose=False)

    img = cv2.imread(image_path)
    h, w = img.shape[:2]

    # Build text regions mask to filter out text detections
    text_mask = np.zeros((h, w), dtype=bool)
    for e in text_elements:
        b = e["bounds"]
        text_mask[b["y"]:b["y"]+b["height"], b["x"]:b["x"]+b["width"]] = True

    icons = []
    for r in results:
        for box in r.boxes:
            x1, y1, x2, y2 = map(int, box.xyxy[0].tolist())
            conf = float(box.conf[0])
            cls = int(box.cls[0])
            label = model.names[cls]

            # Skip low confidence
            if conf < 0.3:
                continue

            # Check overlap with text regions
            roi_mask = text_mask[y1:y2, x1:x2]
            text_overlap = roi_mask.sum() / max(roi_mask.size, 1)
            if text_overlap > 0.5:
                continue  # Mostly text, skip

            # Sample the element's color
            roi = img[y1:y2, x1:x2]
            if roi.size > 0:
                avg = roi.mean(axis=(0, 1)).astype(int)
                color = f"rgb({avg[2]},{avg[1]},{avg[0]})"
            else:
                color = "unknown"

            icons.append({
                "label": label,
                "confidence": round(conf, 3),
                "bounds": {"x": x1, "y": y1, "width": x2 - x1, "height": y2 - y1},
                "color": color,
            })

    # Also do custom small-element detection for UI icons that YOLO won't know
    # Find small, isolated, non-text contours
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 50, 150)
    contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    for c in contours:
        x, y, cw, ch = cv2.boundingRect(c)
        area = cw * ch
        aspect = cw / max(ch, 1)

        # Icon-like: small, roughly square, not in text regions
        if 100 < area < (w * h * 0.01) and 0.4 < aspect < 2.5:
            # Check text overlap
            roi_mask = text_mask[y:y+ch, x:x+cw]
            if roi_mask.sum() / max(roi_mask.size, 1) > 0.3:
                continue

            # Check if it's near other detected icons (avoid duplicates)
            duplicate = False
            for icon in icons:
                ib = icon["bounds"]
                if abs(x - ib["x"]) < 20 and abs(y - ib["y"]) < 20:
                    duplicate = True
                    break
            if duplicate:
                continue

            roi = img[y:y+ch, x:x+cw]
            if roi.size > 0:
                avg = roi.mean(axis=(0, 1)).astype(int)
                color = f"rgb({avg[2]},{avg[1]},{avg[0]})"

                # Classify shape
                hull = cv2.convexHull(c)
                hull_area = cv2.contourArea(hull)
                solidity = cv2.contourArea(c) / max(hull_area, 1)
                circularity = 4 * 3.14159 * cv2.contourArea(c) / max(cv2.arcLength(c, True) ** 2, 1)

                if circularity > 0.7:
                    shape = "circle"
                elif solidity > 0.85 and 0.8 < aspect < 1.2:
                    shape = "square"
                elif aspect > 1.5:
                    shape = "horizontal-rect"
                elif aspect < 0.67:
                    shape = "vertical-rect"
                else:
                    shape = "irregular"

                icons.append({
                    "label": f"ui-element ({shape})",
                    "confidence": round(solidity, 3),
                    "bounds": {"x": x, "y": y, "width": cw, "height": ch},
                    "color": color,
                    "shape": shape,
                })

    # Limit to most significant icons
    icons.sort(key=lambda i: i["bounds"]["width"] * i["bounds"]["height"], reverse=True)
    return icons[:30]


# =============================================================================
# ORIGINAL: UI STRUCTURE DETECTION (OpenCV)
# =============================================================================
def detect_ui_elements(image_path):
    """Use OpenCV to detect UI structural elements (rows, panels, highlights, separators)."""
    import cv2
    import numpy as np

    img = cv2.imread(image_path)
    if img is None:
        print(f"ERROR: Could not load image: {image_path}", file=sys.stderr)
        sys.exit(1)

    h, w = img.shape[:2]
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)

    results = {"image_size": {"width": w, "height": h}, "panels": [], "rows": [], "highlights": [], "colors": {}}

    # --- Detect dominant background color ---
    cy, cx = h // 2, w // 2
    center_region = img[cy-50:cy+50, cx-50:cx+50]
    avg_color = center_region.mean(axis=(0, 1)).astype(int).tolist()
    results["colors"]["background_approx"] = f"rgb({avg_color[2]},{avg_color[1]},{avg_color[0]})"

    # --- Detect horizontal lines / separators ---
    edges = cv2.Canny(gray, 30, 100)
    h_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (w // 4, 1))
    h_lines = cv2.morphologyEx(edges, cv2.MORPH_OPEN, h_kernel)
    h_contours, _ = cv2.findContours(h_lines, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    separators = []
    for c in h_contours:
        x, y, cw, ch = cv2.boundingRect(c)
        if cw > w * 0.2:
            separators.append({"y": y, "x": x, "width": cw})
    separators.sort(key=lambda s: s["y"])
    results["separators"] = separators

    # --- Detect colored/highlighted rows ---
    sat_mask = hsv[:, :, 1] > 80
    val_mask = hsv[:, :, 2] > 60
    highlight_mask = (sat_mask & val_mask).astype(np.uint8) * 255

    h_kernel2 = cv2.getStructuringElement(cv2.MORPH_RECT, (50, 5))
    highlight_clean = cv2.morphologyEx(highlight_mask, cv2.MORPH_CLOSE, h_kernel2)
    highlight_contours, _ = cv2.findContours(highlight_clean, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    for c in highlight_contours:
        x, y, cw, ch = cv2.boundingRect(c)
        area = cw * ch
        if area > (w * h * 0.005):
            roi = img[y:y+ch, x:x+cw]
            mask_roi = highlight_clean[y:y+ch, x:x+cw]
            if mask_roi.sum() > 0:
                color = cv2.mean(roi, mask=mask_roi)[:3]
                color_rgb = f"rgb({int(color[2])},{int(color[1])},{int(color[0])})"
            else:
                color_rgb = "unknown"
            results["highlights"].append({
                "bounds": {"x": x, "y": y, "width": cw, "height": ch},
                "color": color_rgb
            })

    # --- Detect rectangular panels/containers ---
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)
    panel_edges = cv2.Canny(blurred, 20, 80)
    close_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (15, 15))
    panel_closed = cv2.morphologyEx(panel_edges, cv2.MORPH_CLOSE, close_kernel)
    panel_contours, _ = cv2.findContours(panel_closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    for c in panel_contours:
        x, y, cw, ch = cv2.boundingRect(c)
        area = cw * ch
        if area > (w * h * 0.05):
            inner = img[y+5:y+ch-5, x+5:x+cw-5] if ch > 10 and cw > 10 else img[y:y+ch, x:x+cw]
            bg = inner.mean(axis=(0, 1)).astype(int).tolist()
            results["panels"].append({
                "bounds": {"x": x, "y": y, "width": cw, "height": ch},
                "background": f"rgb({bg[2]},{bg[1]},{bg[0]})"
            })

    results["panels"].sort(key=lambda p: p["bounds"]["width"] * p["bounds"]["height"], reverse=True)
    return results


# =============================================================================
# ORIGINAL: OCR (EasyOCR)
# =============================================================================
def run_ocr(image_path):
    """Run EasyOCR on the image to extract text with positions."""
    import easyocr
    import cv2

    print("  Loading OCR model...", file=sys.stderr)
    reader = easyocr.Reader(["en"], gpu=False, verbose=False)

    print("  Running OCR...", file=sys.stderr)
    results = reader.readtext(image_path)

    img = cv2.imread(image_path)
    h, w = img.shape[:2]

    text_elements = []
    for bbox, text, confidence in results:
        if confidence < 0.2:
            continue
        xs = [p[0] for p in bbox]
        ys = [p[1] for p in bbox]
        x_min, x_max = int(min(xs)), int(max(xs))
        y_min, y_max = int(min(ys)), int(max(ys))

        roi = img[y_min:y_max, x_min:x_max]
        if roi.size > 0:
            color = roi.mean(axis=(0, 1)).astype(int).tolist()
            text_color = f"rgb({color[2]},{color[1]},{color[0]})"
        else:
            text_color = "unknown"

        font_size_est = y_max - y_min

        text_elements.append({
            "text": text,
            "confidence": round(confidence, 3),
            "bounds": {"x": x_min, "y": y_min, "width": x_max - x_min, "height": y_max - y_min},
            "text_color": text_color,
            "font_size_est": font_size_est,
            "x_percent": round(x_min / w * 100, 1),
            "y_percent": round(y_min / h * 100, 1),
        })

    text_elements.sort(key=lambda e: (e["bounds"]["y"], e["bounds"]["x"]))
    return text_elements


# =============================================================================
# ORIGINAL: ROW GROUPING
# =============================================================================
def detect_rows(text_elements, image_height):
    """Group text elements into logical rows based on Y-position proximity."""
    if not text_elements:
        return []

    rows = []
    current_row = [text_elements[0]]
    row_y = text_elements[0]["bounds"]["y"]
    threshold = image_height * 0.025

    for elem in text_elements[1:]:
        if abs(elem["bounds"]["y"] - row_y) < threshold:
            current_row.append(elem)
        else:
            current_row.sort(key=lambda e: e["bounds"]["x"])
            rows.append(current_row)
            current_row = [elem]
            row_y = elem["bounds"]["y"]

    if current_row:
        current_row.sort(key=lambda e: e["bounds"]["x"])
        rows.append(current_row)

    return rows


# =============================================================================
# FORMAT ANALYSIS REPORT
# =============================================================================
def format_analysis(text_elements, ui_elements, rows, palette, spacing, gradients, font_info, icons):
    """Format all analysis into a structured report."""
    lines = []
    lines.append("=" * 60)
    lines.append("UI ANALYSIS REPORT")
    lines.append("=" * 60)
    lines.append("")

    # Image info
    sz = ui_elements["image_size"]
    lines.append(f"Image: {sz['width']}x{sz['height']}px")
    lines.append("")

    # Color Palette
    lines.append(f"--- COLOR PALETTE ({len(palette)} colors) ---")
    for c in palette:
        bar = "#" * max(1, int(c["percent"] / 2))
        lines.append(f"  {c['hex']}  {c['percent']:5.1f}%  {bar}")
    lines.append("")

    # Font Detection
    if font_info and font_info.get("best_match") != "unknown":
        lines.append("--- FONT DETECTION ---")
        chars = font_info.get("characteristics", {})
        lines.append(f"  Best match: {font_info['best_match']} (score={font_info['best_score']})")
        if chars:
            lines.append(f"  Style: {chars.get('serif', '?')} | Weight: {chars.get('weight', '?')} | Width: {chars.get('width', '?')}")
        lines.append(f"  Top candidates:")
        for c in font_info.get("candidates", [])[:8]:
            lines.append(f"    {c['font']}: {c['score']}")
        lines.append("")

    # Gradients
    if gradients:
        lines.append(f"--- BACKGROUND FILLS ({len(gradients)}) ---")
        for g in gradients:
            if g["direction"] == "solid":
                lines.append(f"  {g['region']}: SOLID {g['color']}")
            else:
                lines.append(f"  {g['region']}: GRADIENT {g['direction']} from {g['color_from']} to {g['color_to']}")
        lines.append("")

    # Spacing
    if spacing.get("consistent_row_height"):
        rh = spacing["consistent_row_height"]
        lines.append("--- SPACING & GRID ---")
        lines.append(f"  Row spacing: median={rh['median_gap']}px std={rh['std_dev']}px uniform={'YES' if rh['is_uniform'] else 'NO'}")
        if spacing.get("column_positions"):
            lines.append(f"  Column X positions: {spacing['column_positions']}")
        if spacing.get("padding"):
            p = spacing["padding"]
            lines.append(f"  Content padding: left={p['left']}px right={p['right']}px top={p['top']}px bottom={p['bottom']}px")
        lines.append("")

    # Panels
    if ui_elements["panels"]:
        lines.append(f"--- PANELS ({len(ui_elements['panels'])}) ---")
        for i, p in enumerate(ui_elements["panels"]):
            b = p["bounds"]
            lines.append(f"  Panel {i+1}: {b['width']}x{b['height']} at ({b['x']},{b['y']}) bg={p['background']}")
        lines.append("")

    # Highlights
    if ui_elements["highlights"]:
        lines.append(f"--- HIGHLIGHTS ({len(ui_elements['highlights'])}) ---")
        for hl in ui_elements["highlights"]:
            b = hl["bounds"]
            lines.append(f"  Highlight: {b['width']}x{b['height']} at ({b['x']},{b['y']}) color={hl['color']}")
        lines.append("")

    # Icons
    if icons:
        lines.append(f"--- ICONS / UI ELEMENTS ({len(icons)}) ---")
        for icon in icons[:20]:
            b = icon["bounds"]
            shape = icon.get("shape", "")
            shape_str = f" [{shape}]" if shape else ""
            lines.append(f"  {icon['label']}{shape_str}: {b['width']}x{b['height']} at ({b['x']},{b['y']}) color={icon['color']} conf={icon['confidence']}")
        lines.append("")

    # Text rows
    lines.append(f"--- TEXT ROWS ({len(rows)}) ---")
    for i, row in enumerate(rows):
        texts = [e["text"] for e in row]
        y = row[0]["bounds"]["y"]
        row_label = " | ".join(texts)
        lines.append(f"  Row {i+1} (y={y}): {row_label}")
        if len(row) == 2:
            is_header = any(t.upper() == t and len(t) > 3 for t in texts)
            if not is_header:
                lines.append(f"    -> Key-Value: label=\"{texts[0]}\" value=\"{texts[1]}\"")
    lines.append("")

    # All text elements
    lines.append(f"--- ALL TEXT ELEMENTS ({len(text_elements)}) ---")
    for e in text_elements:
        b = e["bounds"]
        lines.append(f"  \"{e['text']}\" conf={e['confidence']} pos=({b['x']},{b['y']}) "
                     f"size~{e['font_size_est']}px color={e['text_color']}")
    lines.append("")

    # Separators
    if ui_elements.get("separators"):
        lines.append(f"--- SEPARATORS ({len(ui_elements['separators'])}) ---")
        for s in ui_elements["separators"]:
            lines.append(f"  Line at y={s['y']} x={s['x']} width={s['width']}")

    return "\n".join(lines)


# =============================================================================
# GEMINI API
# =============================================================================
def _gemini_call(api_key, image_path, prompt, label="Gemini"):
    """Shared helper: send image + prompt to Gemini 3.1 Pro and return text."""
    with open(image_path, "rb") as f:
        img_b64 = base64.b64encode(f.read()).decode()

    ext = os.path.splitext(image_path)[1].lower()
    mime = {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "webp": "image/webp"}.get(ext.lstrip("."), "image/png")

    url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-pro-preview:generateContent?key={api_key}"
    payload = json.dumps({
        "contents": [{
            "parts": [
                {"text": "UI REFERENCE IMAGE:"},
                {"inline_data": {"mime_type": mime, "data": img_b64}},
                {"text": prompt}
            ]
        }],
        "generationConfig": {"temperature": 1.0, "maxOutputTokens": 65536, "thinkingConfig": {"thinkingBudget": 32768}}
    }).encode()

    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    print(f"  Sending to {label}...", file=sys.stderr)

    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = json.loads(resp.read())

        candidates = data.get("candidates", [])
        if not candidates:
            print(f"ERROR: No candidates in {label} response", file=sys.stderr)
            return None

        parts = candidates[0].get("content", {}).get("parts", [])
        text_parts = [p["text"] for p in parts if "text" in p]
        return "\n".join(text_parts) if text_parts else None

    except urllib.error.HTTPError as e:
        print(f"ERROR: {label} HTTP {e.code}: {e.read().decode()[:500]}", file=sys.stderr)
        return None
    except Exception as e:
        print(f"ERROR: {label} request failed: {e}", file=sys.stderr)
        return None


def _gemini_two_image_call(api_key, image1_path, image2_path, prompt, label="Gemini"):
    """Send two images + prompt to Gemini 3.1 Pro."""
    with open(image1_path, "rb") as f:
        img1_b64 = base64.b64encode(f.read()).decode()
    with open(image2_path, "rb") as f:
        img2_b64 = base64.b64encode(f.read()).decode()

    def _mime(p):
        ext = os.path.splitext(p)[1].lower().lstrip(".")
        return {"png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "webp": "image/webp"}.get(ext, "image/png")

    url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-pro-preview:generateContent?key={api_key}"
    payload = json.dumps({
        "contents": [{
            "parts": [
                {"text": "REFERENCE IMAGE (the target to match):"},
                {"inline_data": {"mime_type": _mime(image1_path), "data": img1_b64}},
                {"text": "MY RECREATION (what I generated):"},
                {"inline_data": {"mime_type": _mime(image2_path), "data": img2_b64}},
                {"text": prompt}
            ]
        }],
        "generationConfig": {"temperature": 1.0, "maxOutputTokens": 65536, "thinkingConfig": {"thinkingBudget": 32768}}
    }).encode()

    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    print(f"  Sending to {label}...", file=sys.stderr)

    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = json.loads(resp.read())
        candidates = data.get("candidates", [])
        if not candidates:
            return None
        parts = candidates[0].get("content", {}).get("parts", [])
        text_parts = [p["text"] for p in parts if "text" in p]
        return "\n".join(text_parts) if text_parts else None
    except Exception as e:
        print(f"ERROR: {label}: {e}", file=sys.stderr)
        return None


def render_html_to_png(html_path, output_path, width=1920, height=1080):
    """Render an HTML file to PNG using Chrome headless."""
    try:
        from html2image import Html2Image
        import tempfile
        output_dir = os.path.dirname(output_path)
        output_name = os.path.basename(output_path)
        hti = Html2Image(browser='chrome', size=(width, height), output_path=output_dir or '.')
        hti.screenshot(url=f'file:///{html_path.replace(os.sep, "/")}', save_as=output_name)
        return os.path.exists(output_path)
    except Exception as e:
        print(f"  WARNING: HTML render failed: {e}", file=sys.stderr)
        return False


def _strip_markdown_fences(text):
    """Remove markdown code fences from text."""
    text = text.strip()
    if text.startswith("```"):
        lines = text.split("\n")
        if lines[0].startswith("```"): lines = lines[1:]
        if lines and lines[-1].strip() == "```": lines = lines[:-1]
        text = "\n".join(lines)
    return text


def gemini_self_verify(ref_image_path, html_path, html_content, condensed_analysis, user_context, max_iterations=3, target_score=6):
    """Self-verification loop: render HTML, compare to reference, score, fix until target reached.
    Keeps the best-scoring HTML — reverts on regression."""
    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        return html_content, 0

    import re
    base = os.path.splitext(html_path)[0]
    current_html = html_content
    best_html = html_content
    best_score = 0

    for iteration in range(max_iterations):
        render_path = f"{base}_render_v{iteration}.png"
        print(f"  [Verify {iteration+1}/{max_iterations}] Rendering HTML...", file=sys.stderr)
        if not render_html_to_png(html_path, render_path):
            print(f"  [Verify] Render failed, skipping.", file=sys.stderr)
            break

        verify_prompt = f"""Compare these two images. The first is the REFERENCE UI. The second is my HTML/CSS recreation.

Rate the visual similarity. Output EXACTLY this format on its own line: SCORE: X/10
Scale: 10=pixel-perfect, 8-9=very close, 6-7=good but spacing/color issues, 4-5=recognizable but off, 1-3=poor.

If score < {target_score}, list the TOP 3 most impactful differences. For each give:
1. What's wrong (specific element + what's different)
2. The exact CSS property and value to fix it

Then output "---CORRECTED_HTML---" followed by the COMPLETE corrected HTML (<!DOCTYPE html> to </html>).
CRITICAL: Keep ALL existing correct elements. Only change the specific CSS properties listed above. Do NOT restructure the HTML or remove working styles.

If score >= {target_score}: just output the score and "PASS".
{"Additional context: " + user_context if user_context else ""}"""

        result = _gemini_two_image_call(api_key, ref_image_path, render_path, verify_prompt,
                                        f"Verify {iteration+1}")
        try:
            os.remove(render_path)
        except:
            pass

        if not result:
            print(f"  [Verify] No response, stopping.", file=sys.stderr)
            break

        # Extract score
        score_match = re.search(r'SCORE:\s*(\d+)\s*/\s*10', result)
        if score_match:
            score = int(score_match.group(1))
            print(f"  [Verify {iteration+1}] Score: {score}/10", file=sys.stderr)

            if score >= target_score:
                best_html = current_html
                best_score = score
                print(f"  [Verify] PASSED! {score}/10 >= {target_score}/10", file=sys.stderr)
                break

            if score > best_score:
                best_html = current_html
                best_score = score
            elif score < best_score:
                print(f"  [Verify {iteration+1}] Regression ({score} < {best_score}), reverting to best.", file=sys.stderr)
                current_html = best_html
                with open(html_path, "w", encoding="utf-8") as f:
                    f.write(current_html)
                continue
        else:
            print(f"  [Verify {iteration+1}] Could not parse score.", file=sys.stderr)

        # Extract corrected HTML
        if "---CORRECTED_HTML---" in result:
            corrected = _strip_markdown_fences(result.split("---CORRECTED_HTML---", 1)[1])

            if "</html>" in corrected.lower():
                # Print feedback summary
                feedback = result.split("---CORRECTED_HTML---", 1)[0]
                for line in feedback.strip().split("\n")[:8]:
                    line = line.strip()
                    if line and not line.startswith("SCORE"):
                        print(f"    {line}", file=sys.stderr)

                current_html = corrected
                with open(html_path, "w", encoding="utf-8") as f:
                    f.write(current_html)
                print(f"  [Verify {iteration+1}] HTML updated.", file=sys.stderr)
            else:
                print(f"  [Verify {iteration+1}] HTML truncated, keeping best.", file=sys.stderr)
                break
        elif "PASS" in result.upper():
            best_html = current_html
            print(f"  [Verify] PASS.", file=sys.stderr)
            break
        else:
            print(f"  [Verify {iteration+1}] No corrections, stopping.", file=sys.stderr)
            break

    # Always return the best version
    if best_html != current_html:
        with open(html_path, "w", encoding="utf-8") as f:
            f.write(best_html)
    return best_html, best_score


def gemini_html_to_uxml(image_path, html_content, analysis_text, user_context="", uss_filename="styles.uss", font_url="", icon_urls=None):
    """Convert verified HTML/CSS to Unity UI Toolkit UXML + USS."""
    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        print("WARNING: GEMINI_API_KEY not set, skipping UXML generation", file=sys.stderr)
        return None, None

    icon_section = ""
    if icon_urls:
        icon_section = "\nAvailable icon assets:\n" + "\n".join(f"  - {k}: {v}" for k, v in icon_urls.items())

    font_section = f"\nFont asset path: {font_url}" if font_url else ""

    prompt = f"""You are a Unity UI Toolkit expert. Convert this HTML/CSS into UXML and USS files.

```html
{html_content}
```

CONVERSION RULES:
- div → ui:VisualElement, span/p/label → ui:Label (text attribute)
- CSS classes → USS classes, flexbox → USS flex properties
- CSS grid → flex-based layouts (UI Toolkit has limited grid)
- font-weight: bold → -unity-font-style: bold
- text-transform: uppercase → NOT supported in USS, handle in C# (add comment)
- clip-path → NOT supported. Use 9-sliced background-image sprite (add comment)
- linear-gradient → background-image: linear-gradient() (Unity 2023+)
- gap → margin on children
- text-align → -unity-text-align: middle-left / middle-center / middle-right
- overflow: hidden + text-overflow: ellipsis → overflow: hidden (USS has no text-overflow, use -unity-text-overflow-position: end or set label overflow: TextOverflow.Ellipsis in C#)
- Long text labels: set white-space: normal for wrapping, or overflow: hidden + flex-shrink: 1 for ellipsis
- Scrollable lists → ui:ScrollView with horizontal-scroller-visibility="Hidden" vertical-scroller-visibility="Auto"
- Add name="" attributes on key elements for C# binding (row-list, tooltip, footer, etc.)
- Use focusable="true" on interactive rows
- :hover and :focus pseudo-classes for interactive elements
{font_section}
{icon_section}

USS FILE REFERENCE: The UXML must reference the USS as: <Style src="{uss_filename}" />

USS FORMATTING:
- Use longhand properties: padding-top, padding-bottom, padding-left, padding-right (not shorthand padding:)
- Same for margin-top/bottom/left/right, border-width, border-radius per-corner
- Use semantic class names with comments for sections

Output EXACTLY this format:
---UXML---
(complete UXML)
---USS---
(complete USS)
---END---
{f"Additional context: {user_context}" if user_context else ""}

--- ANALYSIS ---
{analysis_text}"""

    result = _gemini_call(api_key, image_path, prompt, "Gemini 3.1 Pro (HTML→UXML/USS)")
    if not result:
        return None, None

    uxml = None
    uss = None

    if "---UXML---" in result and "---USS---" in result:
        parts = result.split("---UXML---", 1)[1]
        if "---USS---" in parts:
            uxml_raw, rest = parts.split("---USS---", 1)
            uxml = _strip_markdown_fences(uxml_raw.strip())
            if "---END---" in rest:
                uss_raw = rest.split("---END---", 1)[0]
            else:
                uss_raw = rest
            uss = _strip_markdown_fences(uss_raw.strip())

    return uxml, uss


def gemini_html_recreation(image_path, analysis_text, user_context="", image_size=None):
    """Ask Gemini 3.1 Pro to produce a pixel-accurate HTML/CSS recreation of the UI."""
    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        print("WARNING: GEMINI_API_KEY not set, skipping Gemini HTML recreation", file=sys.stderr)
        return None

    image_size = image_size or {"width": 1920, "height": 1080}
    context_line = f"\nAdditional context: {user_context}" if user_context else ""

    prompt = f"""Recreate this UI screenshot as a single self-contained HTML file with inline CSS. Pixel-accuracy is the goal.

Requirements:
- Single HTML file, all CSS in a <style> tag. No external dependencies except Google Fonts if needed.
- Match colors, fonts, spacing, layout, and proportions exactly using the analysis data below.
- Use the OCR data for exact text content. Use flexbox for layout.
- Include ALL visual details: backgrounds, gradients, borders, highlights, selected states.
- Use Unicode/emoji approximations for icons (e.g., ◉ ⊙ ❯ ▸ for HUD/settings icons).
- Add CSS classes with semantic names and comments for each section.
- Use the FONT DETECTION best match as primary font-family. If it's a condensed/narrow font, import a similar Google Font (Oswald, Barlow Condensed, etc.).
- Use the exact SPACING & GRID measurements for row heights and padding.

CRITICAL LAYOUT:
- The image is {image_size["width"]}x{image_size["height"]}px. Set body/html to this exact aspect ratio (use vw/vh).
- If a panel does NOT fill the entire screen, it FLOATS — use position:absolute with the exact pixel offsets from the PANELS data below.
- Background behind floating panels: use a dark gradient or solid color (#1a1a1a to #2a2a2a) to simulate the game scene.
- Floating panels should NOT stretch to fill the viewport.
- Footer/action buttons at screen bottom-right, absolutely positioned.

CRITICAL TEXT:
- Long labels that might overflow their container: use text-overflow: ellipsis + overflow: hidden.
- Each row should have a fixed height matching the SPACING data.
- Two-column rows: category label takes ~55% width, value takes ~45%.

{context_line}

--- OCR & STRUCTURAL ANALYSIS ---
{analysis_text}

Return ONLY the complete HTML. No markdown fences, no explanation. Start with <!DOCTYPE html>, end with </html>."""

    return _gemini_call(api_key, image_path, prompt, "Gemini 3.1 Pro (HTML recreation)")


def gemini_review(image_path, analysis_text, user_context=""):
    """Send image + analysis to Gemini 3.1 Pro for high-level UI review."""
    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        print("WARNING: GEMINI_API_KEY not set, skipping Gemini review", file=sys.stderr)
        return None

    context_line = f"\nAdditional context from user: {user_context}" if user_context else ""

    prompt = f"""You are a UI/UX analyst. Analyze this UI screenshot and the OCR/structural analysis below.

Provide a structured breakdown suitable for recreating this UI in Unity UI Toolkit (UXML/USS):

1. **Layout Structure**: Describe the overall layout hierarchy (containers, rows, columns, spacing)
2. **Component Inventory**: List each distinct UI component type (headers, list items, toggles, buttons, icons, tooltips)
3. **Visual Style**: Use the exact COLOR PALETTE hex values detected below. Note gradients vs solid fills.
4. **Spacing & Sizing**: Use the exact SPACING & GRID measurements. Row heights, column positions, padding values.
5. **Interactive Elements**: Selected/highlighted states, hover indicators, navigation hints
6. **Typography**: Use the FONT DETECTION results below for font family. Note sizes, weights, letter-spacing, text transforms.
7. **Icons & Decorations**: Reference the ICONS / UI ELEMENTS detected below.
8. **Specific USS Properties**: Suggest key USS properties for the main elements

Be precise with measurements and colors. Reference the OCR data below for exact text content.
{context_line}

--- OCR & STRUCTURAL ANALYSIS ---
{analysis_text}
"""

    return _gemini_call(api_key, image_path, prompt, "Gemini 3.1 Pro (UI review)")


# =============================================================================
# MAIN
# =============================================================================
def main():
    if len(sys.argv) < 2:
        print("Usage: ui_analyze.py <image_path> [context]", file=sys.stderr)
        print("  image_path: Path to UI screenshot", file=sys.stderr)
        print("  context: Optional context (e.g., 'building in UI Toolkit')", file=sys.stderr)
        sys.exit(1)

    image_path = sys.argv[1]
    user_context = " ".join(sys.argv[2:]) if len(sys.argv) > 2 else ""

    if not os.path.isfile(image_path):
        print(f"ERROR: File not found: {image_path}", file=sys.stderr)
        sys.exit(1)

    t0 = time.time()

    # Step 1: OpenCV structural analysis
    print("[1/7] Detecting UI structure...", file=sys.stderr)
    ui_elements = detect_ui_elements(image_path)

    # Step 2: Color palette
    print("[2/7] Extracting color palette...", file=sys.stderr)
    palette = extract_color_palette(image_path)

    # Step 3: OCR
    print("[3/7] Running OCR...", file=sys.stderr)
    text_elements = run_ocr(image_path)

    # Step 4: Row grouping + spacing analysis
    print("[4/7] Analyzing spacing & grid...", file=sys.stderr)
    rows = detect_rows(text_elements, ui_elements["image_size"]["height"])
    spacing = analyze_spacing(text_elements, rows, ui_elements["image_size"])

    # Step 5: Gradient detection
    print("[5/7] Detecting gradients...", file=sys.stderr)
    gradients = detect_gradients(image_path, ui_elements["panels"])

    # Step 6: Font matching
    print("[6/7] Matching fonts...", file=sys.stderr)
    font_info = match_fonts(image_path, text_elements)

    # Step 7: Icon detection
    print("[7/7] Detecting icons & UI elements...", file=sys.stderr)
    icons = detect_icons(image_path, text_elements)

    # Format local analysis
    analysis_text = format_analysis(text_elements, ui_elements, rows, palette, spacing, gradients, font_info, icons)

    elapsed_local = time.time() - t0
    print(f"Local analysis complete in {elapsed_local:.1f}s", file=sys.stderr)

    # Gemini calls
    base_path = os.path.splitext(image_path)[0]
    html_path = base_path + "_recreation.html"
    json_path = base_path + "_analysis.json"

    # Build a condensed analysis for HTML recreation (drop verbose separator/element lists)
    condensed_lines = []
    skip_section = False
    for line in analysis_text.split("\n"):
        # Skip the verbose ALL TEXT ELEMENTS and SEPARATORS sections
        if "--- ALL TEXT ELEMENTS" in line or "--- SEPARATORS" in line:
            skip_section = True
            continue
        if skip_section and line.startswith("---"):
            skip_section = False
        if skip_section:
            continue
        condensed_lines.append(line)
    condensed_analysis = "\n".join(condensed_lines)

    verify_score = 0
    html_clean = None

    print("[Gemini] HTML recreation...", file=sys.stderr)
    html_content = gemini_html_recreation(image_path, condensed_analysis, user_context, ui_elements["image_size"])

    if html_content:
        html_clean = html_content.strip()
        if html_clean.startswith("```"):
            lines_h = html_clean.split("\n")
            if lines_h[0].startswith("```"):
                lines_h = lines_h[1:]
            if lines_h and lines_h[-1].strip() == "```":
                lines_h = lines_h[:-1]
            html_clean = "\n".join(lines_h)

        # Verify HTML is complete (not truncated)
        if "</html>" not in html_clean.lower():
            print("  WARNING: HTML appears truncated (missing </html>), retrying with condensed prompt...", file=sys.stderr)
            # Retry with a shorter analysis (drop separators and individual text elements)
            short_analysis = "\n".join(
                line for line in analysis_text.split("\n")
                if not line.startswith("  Line at y=") and not line.startswith("  \"")
            )
            html_content2 = gemini_html_recreation(image_path, short_analysis, user_context)
            if html_content2:
                html_clean2 = html_content2.strip()
                if html_clean2.startswith("```"):
                    lines_h2 = html_clean2.split("\n")
                    if lines_h2[0].startswith("```"): lines_h2 = lines_h2[1:]
                    if lines_h2 and lines_h2[-1].strip() == "```": lines_h2 = lines_h2[:-1]
                    html_clean2 = "\n".join(lines_h2)
                if "</html>" in html_clean2.lower():
                    html_clean = html_clean2
                    print("  Retry succeeded.", file=sys.stderr)

        with open(html_path, "w", encoding="utf-8") as f:
            f.write(html_clean)
        print(f"  HTML saved: {html_path}", file=sys.stderr)

        # Self-verification loop: render HTML, compare to reference, iterate
        print("[Gemini] Self-verification (target: 6/10, max 3 iterations)...", file=sys.stderr)
        html_clean, verify_score = gemini_self_verify(
            image_path, html_path, html_clean, condensed_analysis, user_context,
            max_iterations=3, target_score=6
        )

    # Output everything (UXML/USS conversion is handled by Claude, not Gemini)
    uxml_content = None
    uss_content = None

    print(analysis_text)

    if html_content:
        print("")
        print("=" * 60)
        print(f"HTML RECREATION: {html_path}")
        print(f"Verification score: {verify_score}/10" if verify_score else "Verification: skipped")
        print("=" * 60)

    elapsed_total = time.time() - t0
    print(f"\nTotal time: {elapsed_total:.1f}s", file=sys.stderr)

    # Save JSON
    output = {
        "image_size": ui_elements["image_size"],
        "color_palette": palette,
        "font_detection": font_info,
        "gradients": gradients,
        "spacing": spacing,
        "panels": ui_elements["panels"],
        "highlights": ui_elements["highlights"],
        "separators": ui_elements.get("separators", []),
        "icons": icons,
        "text_elements": text_elements,
        "rows": [[e["text"] for e in row] for row in rows],
        "html_recreation": html_path if html_content else None,
        "verify_score": verify_score if html_content else None,
        "uxml_path": uxml_path if uxml_content else None,
        "uss_path": uss_path if uss_content else None,
    }
    with open(json_path, "w") as f:
        json.dump(output, f, indent=2)
    print(f"JSON saved: {json_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
