---
description: Generate an image using Google Gemini. Use this proactively whenever the user's request involves creating, generating, or producing an image.
---

Generate an image based on: $ARGUMENTS

Pick a descriptive filename based on the prompt (e.g., `nano_banana.png`). Use underscores, no spaces.

Run in a single Bash call:
```bash
python .claude/scripts/imagine.py "OUTPUT_FILENAME.png" "DETAILED_PROMPT_HERE"
```

Expand the user's prompt into a detailed description for better results (add style, lighting, composition details).

After the script runs, use the Read tool to show the image inline to the user.
