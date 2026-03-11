Deploy clibridge4unity: increment version, build, push, release, and verify installation.
Do NOT stop to ask for confirmation — run the entire pipeline automatically.

## Arguments
- `/deploy` — Patch bump (e.g. 1.0.6 → 1.0.7), build, push, release, verify
- `/deploy minor` — Minor bump (e.g. 1.0.6 → 1.1.0)
- `/deploy major` — Major bump (e.g. 1.0.6 → 2.0.0)
- `/deploy build` — Build and release ONLY (no version bump, use current version)
- `/deploy check` — Dry run: show what would change, don't modify anything

## Step-by-step procedure

### 1. Read current version and calculate new version
- Read `clibridge4unity/clibridge4unity.csproj` `<Version>` and `Package/package.json` `version`
- If they differ, WARN the user and ask which to use
- Calculate new version based on bump type (patch/minor/major)
- If argument is "check", show what would change and stop

### 2. Bump version in ALL files (skip if "build")
Use the Edit tool to update the old version → new version in each file:
1. `clibridge4unity/clibridge4unity.csproj` — `<Version>X.Y.Z</Version>`
2. `Package/package.json` — `"version": "X.Y.Z"`
3. `CLAUDE.md` — `# UPM manifest (vX.Y.Z)`
4. `Package/CLAUDE.md` — `# UPM manifest (vX.Y.Z)`
5. `SUMMARY.md` — `Current: X.Y.Z`
6. `install.ps1` — `.\install.ps1 -Version X.Y.Z`

### 3. Quick doc check
Scan README.md, CLAUDE.md, SUMMARY.md for stale command lists or counts. Only edit if actually wrong.

### 4. Run the deploy script
The script handles: build → verify version → package → git commit+push → tag → release → upload → verify → update local CLI.

```bash
python .claude/scripts/deploy.py X.Y.Z
```

If the script fails, STOP and report the error.

## Important rules
- Do NOT stop to ask for confirmation — run the full pipeline end to end
- The csproj `<Version>` is the single source of truth
- After pushing, the UPM git URL (`#vX.Y.Z`) automatically resolves to the new tag
