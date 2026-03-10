---
description: Generate an image using Google Gemini. Use this proactively whenever the user's request involves creating, generating, or producing an image.
---

Generate an image based on: $ARGUMENTS

Use the Bash tool to call the Gemini API. Use a 60-second timeout for the curl call.

Step 1 - Generate:
```bash
curl -s "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-exp-image-generation:generateContent?key=***REDACTED***" \
  -H "Content-Type: application/json" \
  -d '{"contents":[{"parts":[{"text":"THE_PROMPT_HERE"}]}],"generationConfig":{"responseModalities":["TEXT","IMAGE"]}}' \
  -o /tmp/gemini_response.json
```

Step 2 - Extract and save (use `python` not `python3` on Windows):
```bash
TMPFILE=$(cygpath -w /tmp/gemini_response.json) && python -c "
import json, base64
d = json.load(open(r'$TMPFILE'))
if 'error' in d:
    print('ERROR:', d['error']['message'])
else:
    parts = d['candidates'][0]['content']['parts']
    for p in parts:
        if 'inlineData' in p:
            img = base64.b64decode(p['inlineData']['data'])
            with open('OUTPUT_PATH.png', 'wb') as f:
                f.write(img)
            print(f'Saved {len(img)} bytes')
        elif 'text' in p:
            print(p['text'])
"
```

Replace OUTPUT_PATH.png with a descriptive filename in the current working directory based on the prompt (e.g., `nano_banana.png`). Use underscores, no spaces.

After saving, use the Read tool to show the image to the user.
