Deploy clibridge4unity: increment version, build, push, release, and verify installation.
Do NOT stop to ask for confirmation — run the entire pipeline automatically.

## Arguments
- `/deploy` — Patch bump (e.g. 1.0.6 → 1.0.7), build, push, release, verify
- `/deploy minor` — Minor bump (e.g. 1.0.6 → 1.1.0)
- `/deploy major` — Major bump (e.g. 1.0.6 → 2.0.0)
- `/deploy build` — Build and release ONLY (no version bump, use current version)
- `/deploy check` — Dry run: show what would change, don't modify anything

## Step-by-step procedure

### 1. Read current version
- Read `clibridge4unity/clibridge4unity.csproj` and extract the `<Version>` value
- Read `Package/package.json` and extract the `version` value
- If they differ, WARN the user and ask which to use as the base before proceeding
- Display: current version, bump type, and calculated new version
- If argument is "check", show all files that would be updated and stop

### 2. Bump version in ALL files
Update the version string in every file that references it. The canonical source is `clibridge4unity/clibridge4unity.csproj`.

Files to update (use the Edit tool for each):
1. **`clibridge4unity/clibridge4unity.csproj`** — `<Version>X.Y.Z</Version>`
2. **`Package/package.json`** — `"version": "X.Y.Z"`
3. **`CLAUDE.md`** — update `package.json` comment line with version (search for `# UPM manifest`)
4. **`Package/CLAUDE.md`** — update `package.json` comment line with version (search for `# UPM manifest`)
5. **`SUMMARY.md`** — update `Current: X.Y.Z` line
6. **`install.ps1`** — update example comment `.\install.ps1 -Version X.Y.Z`

Skip if argument is "build" (no version bump).

### 3. Update documentation
Before building, review and update documentation if the version or features have changed:
- **`README.md`** — Check if any new commands or changed behavior needs documenting. Don't rewrite unless needed.
- **`CLAUDE.md`** — Check the command list matches reality. Update the `## Commands Available` section if new commands were added recently.
- **`Package/CLAUDE.md`** — Update structure comments if the project structure changed.
- **`SUMMARY.md`** — Update command counts and feature list if they've changed.

Keep documentation changes minimal — only update what's actually stale or wrong.

### 4. Build the CLI
```bash
cd clibridge4unity && dotnet publish -c Release
```
Verify the exe exists at `clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe`.
Report the file size.

### 5. Verify the exe works
Run the built exe with `--version` and confirm it prints the expected new version:
```bash
clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe --version
```
The output MUST contain the new version string. If it doesn't, STOP and fix.

### 6. Package and stage release assets
```bash
# Create zip — use single-quoted powershell command to avoid bash $env expansion issues
powershell -Command 'Compress-Archive -Path "clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe" -DestinationPath "$env:TEMP/clibridge4unity-win-x64.zip" -Force'

# Copy exe to Package/Tools for UPM users
cp clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe Package/Tools/win-x64/
```

### 7. Git commit and push
Stage ALL changed files, commit, and push immediately (no confirmation needed):
```bash
git add -A
git commit -m "Release vX.Y.Z"
git push origin main
```
Use the actual version in the commit message.

### 8. Create git tag and GitHub release
```bash
git tag vX.Y.Z
git push origin vX.Y.Z
gh release create vX.Y.Z --title "vX.Y.Z" --generate-notes
```
If the tag or release already exists, skip that step (don't error).

### 9. Upload release assets
```bash
# Get TEMP path for bash context
TEMP_WIN=$(powershell -Command 'Write-Host $env:TEMP -NoNewline')
gh release upload vX.Y.Z "${TEMP_WIN}/clibridge4unity-win-x64.zip" --clobber
gh release upload vX.Y.Z "clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe" --clobber
```

### 10. Verify the release
Run these verification checks:
1. **Assets uploaded**: `gh release view vX.Y.Z --json assets -q ".assets[].name"` should list both files
2. **Install script URL accessible**:
   ```bash
   curl -sI "https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1" | head -1
   ```
   Should return HTTP 200.

### 11. Report results
Print a summary:
- Version: old → new
- Release URL: `https://github.com/oddgames/clibridge4unity/releases/tag/vX.Y.Z`
- Assets: list with sizes
- Install command: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`
- All verification checks passed/failed

## Important rules
- Do NOT stop to ask for confirmation — run the full pipeline end to end
- If any step FAILS, STOP and report — don't continue with a broken state
- The csproj `<Version>` is the single source of truth — all other files derive from it
- The CLI reads version from its own assembly at runtime, so updating the csproj is sufficient for the exe
- After pushing, the UPM git URL (`#vX.Y.Z`) automatically resolves to the new tag
