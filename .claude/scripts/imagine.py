#!/usr/bin/env python
"""Generate an image using Google Gemini API, save it, and open in VS Code."""
import sys, os, json, base64, subprocess, urllib.request, urllib.error

def main():
    if len(sys.argv) < 3:
        print("Usage: imagine.py <output_path.png> <prompt>", file=sys.stderr)
        sys.exit(1)

    output_path = sys.argv[1]
    prompt = " ".join(sys.argv[2:])

    api_key = os.environ.get("GEMINI_API_KEY", "")
    if not api_key:
        print("ERROR: GEMINI_API_KEY environment variable not set", file=sys.stderr)
        sys.exit(1)

    # Call Gemini API
    url = f"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-exp-image-generation:generateContent?key={api_key}"
    payload = json.dumps({
        "contents": [{"parts": [{"text": prompt}]}],
        "generationConfig": {"responseModalities": ["TEXT", "IMAGE"]}
    }).encode()

    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            data = json.loads(resp.read())
    except urllib.error.HTTPError as e:
        print(f"ERROR: HTTP {e.code}: {e.read().decode()}", file=sys.stderr)
        sys.exit(1)

    if "error" in data:
        print(f"ERROR: {data['error']['message']}", file=sys.stderr)
        sys.exit(1)

    # Extract image and text
    saved = False
    for part in data["candidates"][0]["content"]["parts"]:
        if "inlineData" in part:
            img = base64.b64decode(part["inlineData"]["data"])
            with open(output_path, "wb") as f:
                f.write(img)
            print(f"Saved {len(img)} bytes to {output_path}")
            saved = True
        elif "text" in part:
            print(part["text"])

    if not saved:
        print("ERROR: No image in response", file=sys.stderr)
        sys.exit(1)

    # Open in VS Code
    try:
        code_cmd = None
        for d in os.environ.get("PATH", "").split(os.pathsep):
            candidate = os.path.join(d.strip(), "code.cmd")
            if os.path.isfile(candidate):
                code_cmd = candidate
                break
        if not code_cmd:
            return

        # Find git root for workspace targeting
        git_root = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True
        ).stdout.strip()

        args = [code_cmd, "-r", git_root, output_path] if git_root else [code_cmd, output_path]
        subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        pass

if __name__ == "__main__":
    main()
