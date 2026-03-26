#!/usr/bin/env python
"""Compare two images using Gemini 2.5 Pro for visual feedback."""
import sys, os, json, base64, urllib.request, urllib.error

def main():
    if len(sys.argv) < 4:
        print("Usage: gemini_compare.py <reference.png> <render.png> <prompt>", file=sys.stderr)
        sys.exit(1)

    ref_path = sys.argv[1]
    render_path = sys.argv[2]
    prompt = " ".join(sys.argv[3:])

    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        print("ERROR: GEMINI_API_KEY not set", file=sys.stderr)
        sys.exit(1)

    # Read images
    with open(ref_path, "rb") as f:
        ref_b64 = base64.b64encode(f.read()).decode()
    with open(render_path, "rb") as f:
        render_b64 = base64.b64encode(f.read()).decode()

    url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-pro-preview:generateContent?key={api_key}"
    payload = json.dumps({
        "contents": [{
            "parts": [
                {"text": "REFERENCE IMAGE (the target to match):"},
                {"inline_data": {"mime_type": "image/png", "data": ref_b64}},
                {"text": "MY RECREATION (what I built in Unity UI Toolkit):"},
                {"inline_data": {"mime_type": "image/png", "data": render_b64}},
                {"text": prompt}
            ]
        }],
        "generationConfig": {"temperature": 1.0, "maxOutputTokens": 16384, "thinkingConfig": {"thinkingBudget": 32768}}
    }).encode()

    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.loads(resp.read())
        # Extract text from response, handling various response structures
        candidates = data.get("candidates", [])
        if not candidates:
            print(f"ERROR: No candidates in response: {json.dumps(data, indent=2)}", file=sys.stderr)
            sys.exit(1)
        parts = candidates[0].get("content", {}).get("parts", [])
        text_parts = [p["text"] for p in parts if "text" in p]
        if text_parts:
            print("\n".join(text_parts))
        else:
            # Maybe the response has a different structure
            print(f"Response: {json.dumps(candidates[0], indent=2)}")
    except urllib.error.HTTPError as e:
        print(f"ERROR: HTTP {e.code}: {e.read().decode()}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: {e}\nRaw response: {json.dumps(data, indent=2)[:2000]}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
