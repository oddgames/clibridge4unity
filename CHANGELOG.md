# Changelog

## v1.1.62 — 2026-06-30

## v1.1.62

### Internal
This release is skill-content only — updates to the embedded per-task skills (unpacked into `.claude/skills/` on SETUP/UPDATE). No CLI or bridge behavior changes.

- **UI skills — mobile input ergonomics.** `clibridge4unity-ugui` and `clibridge4unity-ui-toolkit` now cover sizing/placing touch input (vehicle controls, twin sticks, buttons) by physical distance via `Screen.dpi` rather than pixels: ≥9 mm tap targets, thumb-reach zones, ≥2 mm spacing, `Screen.safeArea` insets, and the caveat that `CanvasScaler`/`PanelSettings` scale resolution but not physical size.
- **UI skills — verify at multiple device resolutions.** Render UI at ≥1 phone and ≥1 tablet aspect and present both screenshots back to the user. Added a device resolution table + the `GAMEVIEW WxH` → `SCREENSHOT gameview --output …` workflow to `clibridge4unity-screenshot` (with the gotcha that `gameview` overwrites its temp PNG, so each device pass needs its own `--output`); reinforced in the uGUI/UI-Toolkit/UI skills.
- **UI skills — inherit the game's existing style.** uGUI: inspect a nearby panel/button/toggle prefab and reuse its sprites/fonts/colours/layout (or instantiate a shared prefab/variant). UI Toolkit: `Grep '<Style src='` to find the shared token/theme USS the rest of the game imports, import it first, and reuse its existing classes instead of writing a parallel style.
- **Shader skill — never `COMPILE` for shader edits.** `clibridge4unity-shaders` now states plainly that `COMPILE` recompiles C# (domain reload, breaks pipe) and does NOT compile HLSL — `.shader`/`.cginc`/`.hlsl` edits are asset reimports, so `REFRESH` + `LOG errors` is the shader-compile loop. Corrected the prior contradicting "run a single COMPILE" guidance.
- **Shader skill — mobile ALU micro-optimization.** New section on `UNITY_BRANCH`/`UNITY_FLATTEN` (real branch only on uniform conditions guarding expensive work), loop unrolling (`UNITY_UNROLL` for compile-time-constant counts, `UNITY_LOOP` otherwise), and vectorizing scalar math into matrix `mul`s for SIMD — with a worked terrain-layer before/after example (pre-pack matrix rows into uniforms CPU-side; per-channel packing + silent-no-op pitfalls called out).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.61 — 2026-06-22

## v1.1.61

### New
- `CODE_SEARCH` now works as a CLI-side alias for `CODE_ANALYZE` — the former command name is normalized to the canonical `CODE_ANALYZE` before dispatch, so old scripts and muscle memory keep working. The alias is surfaced in the CLI help text.

### Internal
- Added a "Code Organization" guidance section to `CLAUDE.md`: prefer extending existing types over creating a new class/file for small (one- or two-method) additions; split out only for genuine reusable utilities or when inlining would bloat the host class.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.60 — 2026-06-21

## v1.1.60

### New
- **`UPDATE` now refreshes the per-task skills too.** Previously `UPDATE` self-updated the CLI exe and the UPM package tag but left the on-disk `.claude/skills/` stale, so new skill content baked into the exe never reached the project until the user re-ran `SETUP`. `UPDATE` now re-unpacks the embedded skills into `.claude/skills/` in both update paths (exe-updated and already-up-to-date), using the same wipe-and-reinstall as `SETUP` — shipped `clibridge4unity-*.md` files are replaced; renamed/user-authored skills are left untouched. Silent on skills when run outside a Unity project.

### Internal
- New `RefreshProjectSkills()` helper mirrors `RefreshProjectAgentDocs()` (auto-detects the project, calls `InstallSkills`); wired into both branches of `HandleSelfUpdate`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.59 — 2026-06-21

## v1.1.59

### Skills
- **`clibridge4unity-shaders`** — added a "Editing a shared material at runtime" section covering runtime grading/uniform-tweaking: never `Set*` a shared/Addressable material (instance it with `new Material(shared)` + `CopyPropertiesFromMaterial` + destroy in `OnDestroy`); `MaterialPropertyBlock` works for renderers but not skyboxes; expose a new neutral-default property instead of rebinding a non-neutral engine prop; and the direction-sampled-cubemap uniform-scale no-op. Reworked the keyword-gating guidance into a complexity-gated rule: run cheap always-on math unconditionally (1 variant), **always** gate an optional feature with real per-pixel complexity (compile it out when off — no unconditional run, no uniform `if`), and keep total variants low by coalescing related toggles into one `[KeywordEnum]` rather than N independent booleans (2ᴺ). Runtime-created materials use `multi_compile` (not `shader_feature`, which gets stripped). Frontmatter triggers extended for runtime-grading tasks.

### Internal
- (repo-only, not shipped) `ui-analyze` skill: added a drag-to-scroll `PointerManipulator` pseudo-code example to the UI Toolkit guide's ScrollView section — touch-style panning with click-vs-drag threshold, deferred pointer capture, elastic overshoot/snap-back, and release-outside-bounds handling.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.58 — 2026-06-18

## v1.1.58

### New
- New shipped skill **`clibridge4unity-imagemagick`** — installs ImageMagick if the `magick` command is missing (winget/brew/apt + decode-delegate notes) and documents the high-value editing operations: inspect/identify, resize/crop/extent, format conversion (png/jpg/webp/gif/heic/svg/pdf), compression, rotate/flip/auto-orient, color/transparency, composite + text annotation, batch (`mogrify`), montage/contact-sheet, and GIF assembly. Pairs with `SCREENSHOT` output for cropping/annotating/resizing. Embedded into the CLI exe and unpacked by `SETUP` like every other per-task skill.

### Internal
- Added a repo-only authoring skill (`.claude/skills/cli-skill/`) documenting the convention for creating a new shippable bridge skill (file location, `clibridge4unity-` prefix, csproj glob embedding, SETUP unpack, deploy-bumps-version). Not shipped to users.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.57 — 2026-06-18

## v1.1.57

### Fixed
- **LINT/CODE_ANALYZE bound against stale PackageCache duplicates.** When a UPM package updated, Unity left the old `name@<oldhash>` folder in `Library/PackageCache` beside the live `name@<newhash>`. LINT scanned every cache subdir and deduped DLLs by filename, so the alphabetically-first folder won — often the stale one. Symptom: phantom errors like `CS1739: The best overload for 'Goal' does not have a parameter named 'category'` on source that was actually correct. LINT now scans only the package dirs Unity actually resolved (read from `Library/PackageManager/ProjectCache`), falling back to a full scan only on a fresh pre-resolve checkout. Fixes both `LINT unity` and `LINT semantic`, the asmdef graph, and the CODE_ANALYZE DLL index.
- **Roslyn daemon served stale code after a rebuild without a version bump.** The daemon-staleness check compared only the assembly version string, so an in-place rebuild (same version) reused the old daemon and kept serving pre-fix results. The version token now combines the assembly version with the running exe's last-write time, so a local rebuild auto-kills and respawns the daemon. Released version bumps still trigger it as before; same-binary invocations still reuse the daemon (no thrashing).

### Internal
- New shared helper `LintAsmdef.ResolvedPackageCacheDirs()` parses `resolvedPath:` entries from Unity's `ProjectCache`; consumed by `LintSemantic`, `LintAsmdef.Build`, and `DllIndex`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.56 — 2026-06-17

## v1.1.56

### New
- `clibridge4unity-editor-tools` skill: added two house conventions — **persistence by lifetime** (`EditorPrefs` for secrets / per-user info; `SessionState` for state that must survive a domain reload but not a Unity restart, with the don't-cross-them warning) and **virtualized lists** (many-item editor windows use a data-bound `ListView`, never a per-row `foreach`).
- `clibridge4unity-bridge` routing table now links `clibridge4unity-peers` (responding to `[conflict] WARNING:` when several windows share one editor).

### Fixed
- Release-notes correction: v1.1.55 stated the `clibridge4unity-peers` skill was *removed*. It was actually **rewritten to passive-only** and shipped — it documents the automatic `[conflict]` warnings + the edit HOOK that remain (the `PEERS`/`PEER_SEND`/`PEER_INBOX` commands and the messaging layer are gone; the skill correctly describes no peer commands and only `.peer`/`.active` storage). This release adds its bridge routing row so it's discoverable.

### Internal
- Docs/skills only — no CLI or bridge-command changes this release (still 47 bridge commands).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.55 — 2026-06-16

## v1.1.55

### New
- `COMPONENT_SET` now parses compact compound values: `"x,y,z"` → `Vector2/3/4` and `"#RRGGBB"`/`#RRGGBBAA`/named colors → `Color`/`Color32`. The plain-args forms the docs (and the command's own Usage string) advertised previously failed Newtonsoft struct binding — they now work, with a clear error on malformed input.
- New skill **`clibridge4unity-build`** — documents the `BUILD` command (active-target player builds, `--run`/`--dev`/`--output`, auto-block behavior, reading `[Preprocess Player]`/`[err]` failure lines).
- New skill **`clibridge4unity-ugui`** — classic uGUI / Canvas editing via the bridge: author-as-prefab discipline, `COMPONENT_*`/`PREFAB_*` editing path, and the raycast-target + layout/TMP word-wrap gotchas.
- `clibridge4unity-bridge` now documents `MENU`, `CANCEL`, `BRIDGEINFO`, and `SETUP`/`UPDATE`/`VSCODE`/`OPEN`; `clibridge4unity-performance` now documents the `PROFILE` command (subcommands + `min`/`depth`/`frame`/`thread` filters).

### Changed / Removed
- Removed the `PEERS` / `PEER_SEND` / `PEER_INBOX` CLI commands and the inter-window messaging layer (inbox/cursor, `[peers] MESSAGE` auto-surface). **Automatic cross-window safety is unchanged**: the `[conflict] WARNING` before COMPILE/REFRESH/PLAY/STOP/BUILD/asset-writes and the PreToolUse edit `HOOK` (`CLIBRIDGE_BLOCK_ON_CONFLICT`) remain. Removed the now-obsolete `clibridge4unity-peers` skill.

### Fixed
- Skill accuracy pass across the whole suite: corrected the `UI_DISCOVER` description (it is an alias of `ASSET_DISCOVER ui` and does not enumerate UXML/USS/TSS), the `SCREENSHOT` UXML render size + the scope of the 1280px cap, URP 17 / RenderGraph command-buffer guidance, the `MenuItem` validator syntax, and factual errors in the addressables / input-system / render-textures / compute-shaders / shaders skills.
- Repaired ~57 broken inter-skill cross-references (legacy `unity-*` → `clibridge4unity-*`, plus links to skills that never existed).

### Internal
- Trimmed the installable skill set ~64% (≈2,900 → ≈1,100 lines) by removing general-Unity-knowledge content the consumer model already has, keeping only project-specific behavior, bridge mechanics, and house conventions. `SETUP` continues to wipe-and-refresh only `clibridge4unity-*` skills, leaving user-authored skills untouched.
- Corrected the bridge-command count in `SUMMARY.md` (46 → 47).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.54 — 2026-06-15

## v1.1.54

### New
- **14 generic Unity-workflow skills** ship in the CLI exe and unpack via `SETUP` into every project's `.claude/skills/`. Each follows the same shape (frontmatter with auto-trigger keywords → "the one rule" → pitfall catalog with `Real bug` / `Rule` per item → workflow checklist → quick reference). Topics:
  - `clibridge4unity-async-mainthread` — Task / async / UniTask, main-thread hops, blocking-IO traps, deadlock by `.Result`, hooks the `CLIBridge.SwitchToMainThread/Background/Token` work shipped in 1.1.53.
  - `clibridge4unity-domain-reload` — what survives a reload (SessionState/EditorPrefs/asset files) vs what dies (static fields, async tasks, callbacks, pipe handles), the `[InitializeOnLoad]` defer-to-first-tick pattern, PlayMode-spanning state on disk.
  - `clibridge4unity-serialization` — `[SerializeField]`, `[SerializeReference]` polymorphism, `ISerializationCallbackReceiver`, `[FormerlySerializedAs]` migrations, `SerializableDictionary` template, Sirenix/Odin trade-offs.
  - `clibridge4unity-prefab-workflow` — asset vs instance vs prefab-mode contexts, `PrefabUtility.LoadPrefabContents` + `UnloadPrefabContents` in try/finally, variants and nested-prefab override semantics, missing-script recovery.
  - `clibridge4unity-performance` — `Profiler.BeginSample` / `ProfilerMarker` discipline, custom `ObjectPool<T>` + scoped `Disposable`, cached `LayerMask.GetMask` / string-format avoidance, `Physics.RaycastNonAlloc`, `MaterialPropertyBlock`, Burst+NativeArray patterns.
  - `clibridge4unity-editor-tools` — `SerializedObject.ApplyModifiedProperties`, `Undo.RecordObject`, `[CanEditMultipleObjects]`, UI Toolkit `CreateGUI` skeleton, safe `AssetPostprocessor.OnPostprocessAllAssets`.
  - `clibridge4unity-input-system` — old `Input.*` vs new `InputAction*`, action enable/disable + subscribe/unsubscribe symmetry, action-map switching, `EnhancedTouch`, when to use `PlayerInput` vs explicit subscriptions.
  - `clibridge4unity-addressables` — `SafeRelease(handle)` discipline, `Addressables.InstantiateAsync` + `ReleaseInstance` vs `LoadAssetAsync` + `Release`, refcounted owner pattern, `WaitForCompletion` deadlock inside continuations.
  - `clibridge4unity-shaders` — Built-in RP / CGPROGRAM authoring (skeleton + `#pragma` block), editor-vs-device pitfall catalog (per-platform texture size, lossy compression on categorical textures, `fixed`/`half` precision, multi-SubShader high/low paths, shader-tag → C# string contract).
  - `clibridge4unity-compute-shaders` — `[numthreads]` mobile ceiling, `RWStructuredBuffer` binding limits on gles3/Metal, `ComputeBuffer` lifetime + GPU-memory leaks, `AsyncGPUReadback` vs blocking `GetData`, kernel skeleton.
  - `clibridge4unity-command-buffers` — Built-in (`Camera.AddCommandBuffer` once in OnEnable) vs URP (`ScriptableRendererFeature`), Frame Debugger workflow, `cmd.GetTemporaryRT` vs C# pool (don't mix), compute dispatch inside a CommandBuffer.
  - `clibridge4unity-render-textures` — `GetTemporary`/`ReleaseTemporary` vs `new RT() + Create()`, `enableRandomWrite`, depth attachment choices, sRGB vs linear, save/restore `RenderTexture.active`, format cheat sheet.
  - `clibridge4unity-ui-toolkit` — USS first, then `[UxmlElement]`, then inline C# last resort; `EnableInClassList("hidden", …)` over `style.display`; `schedule.Execute(...).Every(ms)` over `EditorApplication.update`; tracked-subscription `UXMLController` base; sanctioned exceptions (aspect-ratio polyfill, safe-area inset).
  - `clibridge4unity-icons` — react-icons.github.io as the catalogue, render upstream SVG → white PNG @ 4× via `magick -density 600`, tint via USS `-unity-background-image-tint-color`, `AssetPostprocessor` to lock import settings.

### Internal
- **`SETUP` always refreshes shipped skills now.** All shipped skills are renamed to `clibridge4unity-<topic>.md` (the 11 existing usage skills + the 14 new knowledge skills = 25 total). `SETUP` wipes every `.claude/skills/clibridge4unity-*.md` file *and* any legacy file carrying the old `<!-- clibridge4unity:installed-sha=… -->` marker, then re-installs the embedded set fresh. Files without the prefix or marker are user-authored — never touched. To keep a customised copy of a shipped skill, rename it.
- The previous per-file SHA-marker / "locally modified — keep" logic is gone. Reinstall is unconditional; the source of truth is whatever the current CLI ships.
- Old `unity-*.md` files installed by pre-1.1.54 versions are wiped on first run of the new `SETUP` (one-time legacy cleanup).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.53 — 2026-06-10

## v1.1.53

### New
- **`CANCEL` bridge command** — `CANCEL <NAME>` cancels in-flight commands by name, `CANCEL --all` cancels every non-self entry. Bypasses the gate so it always answers, even while the main thread is wedged. Trips each entry's CancellationTokenSource — InvokeOnMainThread sees the cancellation on its next polling tick and TrySetCanceled's the queued work, which ProcessAllPendingWork then skips. Frees queued main-thread work; an action already mid-execute can't be interrupted (the response says so and points at `KILL`).

### Fixed
- **Abrupt CLI exit no longer leaves Unity's main thread queued behind a dead client.** The heartbeat task now watches `pipe.IsConnected` and the write path; either signal trips `timeoutCts` on the command, so a Ctrl+C / killed CLI immediately frees that command's queued main-thread work instead of waiting the full per-command timeout (10–30s).
- **"Unity isn't open" fast-fail is now near-instant (~0.16s, ~190× faster than before).** New `BridgePipeAlive` probe (sub-ms `File.Exists \\.\pipe\<name>`) runs before any state detection — pipe-using commands fast-fail with a 3-line actionable message ("pipe not responding / Unity may not be open / OPEN or DIAG") and skip the ~5s `DetectUnityProcess` call entirely. CLI-side commands (DISMISS, OPEN, KILL, WAKEUP, LAST, LINT, CODE_ANALYZE, SCREENSHOT) bypass the gate and keep working.

### Internal
- `PIPE_CONNECT_TIMEOUT_MS` 10000 → 2000. With the new pipe probe doing the up-front gating, the connect-retry budget only needs to cover legit "bridge just-started" cases — 2s is plenty.
- `DetectUnityProcess` early-outs: ① if THIS project's bridge pipe is alive, return `Running` immediately (skip Restart-Manager lookup + cross-machine window enumeration — typically ~5s saved); ② if the lockfile exists but is *stale* (not held), skip the Restart-Manager RPC (the slowest piece — it serializes against the Windows SCM).
- `CommandRegistry.InFlightEntry` now carries the command's `CancellationTokenSource` so `CANCEL` and disconnect-detection can trip it directly.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.52 — 2026-05-29

## v1.1.52

### New
- **VSCode/Cursor status-bar extension** (`vscode-extension/`, PR #5 by @JPerryOddGames): shows bridge status with a version check + one-click update button when the CLI is stale, plus COMPILE and STATUS buttons. Built to a `.vsix` and embedded in the CLI exe (version-locked to the CLI).
- `VSCODE` command (CLI-side, no Unity needed) — installs the bundled extension into detected editors (`code`/`code-insiders`/`cursor`/`codium`/`windsurf`) via `--install-extension … --force`. Idempotent: skips if an equal-or-newer version is already installed. `SETUP` now points users here.
- `BRIDGEINFO` command — a stable, no-main-thread handshake exposing `bridgeVersion`, `minCompatibleExtensionVersion`, and `bridgeProtocol`. The extension reads it to fail-closed (hide its buttons + offer re-install) when it's older than the bridge's compatibility floor. Treated as a frozen, append-only contract.

### Internal
- `BridgeServer.MinCompatibleExtensionVersion` (starts at `0.0.0`) gates extension compatibility; raise it only in a release that breaks a verb the extension sends or a `STATUS`/`BRIDGEINFO` field it reads.
- `deploy.py` now builds the VSCode extension `.vsix` and stages it into `clibridge4unity/vscode/` for embedding before publish.
- Bridge command count: 45 → 46 (BRIDGEINFO).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.51 — 2026-05-28

## v1.1.51

### New
- `CLIBridge` utility for CODE_EXEC / CODE_EXEC_RETURN snippets: `await CLIBridge.SwitchToBackground()` hops onto a threadpool thread for blocking work (HTTP, sockets, large file IO), `await CLIBridge.SwitchToMainThread()` returns to the Unity main thread for API calls, and `CLIBridge.Token` is a per-exec cancellation token to hand cancellable APIs. Any snippet that uses `await` is auto-compiled as async; sync snippets are unchanged.
- Diagnostics now name what's blocking the bridge: `DIAG` (and busy responses) report `executingMainThreadWork` (the command currently running on the main thread + how long) and `inFlight` (every command in flight), so you can see exactly what's wedging Unity and avoid re-issuing it.

### Fixed
- Diagnostics no longer hang when a command wedges the main thread. `DIAG`/`PING`/`PROBE`/`LOG`/`STATUS` now bypass the execution gate and answer on a background thread within ~1s, instead of timing out behind the stuck command (which previously made the whole bridge look dead).
- One long-running command no longer blocks every other command. Replaced the single global command slot (which serialized all execution) with per-command-type handling: different command types run concurrently. Main-thread work still serializes naturally on the main-thread queue.

### Internal
- Same-type de-dup to prevent the AI double-firing: a duplicate of a command already running for < 5s is rejected with a "already running, no double-up" report naming what's in flight; a re-fire after 5s is treated as an intentional retry and allowed.
- HELP for CODE_EXEC / CODE_EXEC_RETURN documents the SwitchToBackground/SwitchToMainThread/Token pattern.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.50 — 2026-05-27

## v1.1.50

### Fixed
- **`TEST` command spammed `ObjectDisposedException: Cannot access a closed pipe` into Unity's console** when the CLI client disconnected mid-run or when Unity's test framework crashed internally (e.g. `ReloadScene cannot be used with a scene without a SceneAsset`, `Test tree is not available for PostbuildCleanupTask`). `TestRunner.Run` and `RunTests` were calling `writer.WriteLineAsync` for progress/summary/error output without guarding against a closed pipe, so once the pipe was gone every write threw and the outer catch handler itself rethrew. Resulting log lines bubbled all the way up through `CommandRegistry.InvokeCommand` as `[Bridge] Command 'TEST' failed` errors.
- All `writer.WriteLineAsync` calls in `TestRunner` now go through a new `SafeWriteLineAsync` helper that swallows `ObjectDisposedException` and `IOException` (pipe-closed / broken-pipe).
- `TestRunner.Run` catches `ObjectDisposedException` / `IOException` at the outer level and exits silently — the streaming callbacks already swallowed their own writes, but a final summary or error write was still escaping.
- `TestRunner.Run` now has a `finally` block that always unregisters our `StreamingTestCallbacks` from `TestRunnerApi` so a test-framework crash mid-run can't keep firing callbacks at a stale `RunState` after the pipe is gone.

### Notes
- This is a logging hygiene fix only. The underlying Unity test framework errors (`ReloadScene` / `Test tree is not available`) are not from the bridge — they originate inside `com.unity.test-framework` when it encounters a scene without a SceneAsset. The bridge no longer amplifies them with its own noisy stack traces.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.49 — 2026-05-26

## v1.1.49

### Fixed
- **SETUP did not actually refresh UPM when the package version changed** — re-running `clibridge4unity SETUP` after a CLI update would rewrite the git ref in `Packages/manifest.json` but UPM would silently keep using the cached commit hash recorded in `Packages/packages-lock.json`. Symptoms: compile errors against an outdated package (e.g. UI / TestRunner errors that were already fixed upstream), `Library/PackageCache/au.com.oddgames.clibridge4unity@<old-hash>` continuing to be referenced.
- `EnsureUpmPackage` now calls a new `InvalidateUpmCache` step whenever it changes the URL in `manifest.json`:
  - Surgically removes our package's entry from `Packages/packages-lock.json` (leaves other packages untouched) so UPM re-resolves the new git ref.
  - Deletes `Library/PackageCache/au.com.oddgames.clibridge4unity@*` directories so UPM refetches from GitHub rather than reusing the stale clone.
  - Both ops are best-effort — locked files/dirs are skipped silently so the user can clear them manually.

### Notes
- If you're upgrading from v1.1.47 / v1.1.48 and your project is still hitting the UnityEngine.UI / TMPro / TestRunner compile errors, run `clibridge4unity SETUP` once more under v1.1.49 — it will now perform the cache invalidation that was missing. Unity will re-resolve on next focus.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.48 — 2026-05-26

## v1.1.48

### Fixed
- **Package compiled cleanly only by accident** — UPM package now declares the dependencies it actually needs:
  - Added `com.unity.ugui` so `UnityEngine.UI` types (Button, Image, Text, Canvas, GraphicRaycaster) used by `UICommands` and `RenderCommand` resolve in fresh projects.
  - Added `com.unity.test-framework` so the Test Runner API types (`TestRunnerApi`, `ITestAdaptor`, `ITestResultAdaptor`, `ICallbacks`, `TestMode`) used by `TestRunner` resolve in fresh projects.
  - Added `UnityEditor.TestRunner` and `UnityEngine.TestRunner` to the `clibridge4unity.Commands.Code` asmdef references — they were missing entirely, so the file only compiled when the test framework happened to be present in the host project.
- Symptom on a fresh Unity 6 project after `clibridge4unity SETUP`:
  - `CS0234: The type or namespace name 'UI' does not exist in the namespace 'UnityEngine'`
  - `CS0246: The type or namespace name 'TMPro' could not be found` (resolved transitively through `com.unity.ugui` 2.x on Unity 6)
  - `CS0234: The type or namespace name 'TestTools' does not exist in the namespace 'UnityEditor'`
  - `CS0246: TestRunnerApi / ITestAdaptor / TestMode / ICallbacks could not be found`

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.47 — 2026-05-26

## v1.1.47

### New
- `SETUP` now installs **12 task-focused skill files** into `.claude/skills/` so Claude Code (and any other skill-aware assistant) can route to per-task guidance instead of a single fat reference:
  - `unity-bridge` — orientation: when to use which skill, connection, troubleshooting
  - `unity-run-code` — `CODE_EXEC` / `CODE_EXEC_RETURN` (with file-path tip, `--inspect`, `--trace`)
  - `unity-find-code` — `CODE_ANALYZE` (symbol-aware search, offline)
  - `unity-lint` — `STATUS` / `LINT` / `COMPILE` discipline (reactive, not routine)
  - `unity-scene` — scene hierarchy, GameObjects, play mode, scene/game view
  - `unity-components` — `COMPONENT_SET` / `ADD` / `REMOVE`, `INSPECTOR`
  - `unity-prefab` — `PREFAB_*` and prefab-asset inspection
  - `unity-assets` — `ASSET_*` (search, discover, move, copy, delete, label, reserialize)
  - `unity-screenshot` — all `SCREENSHOT` routing modes
  - `unity-ui` — UI Toolkit workflow (`UI_DISCOVER`, UXML rendering, USS import errors)
  - `unity-tests` — `TEST` modes and filters
  - `unity-build` — `BUILD` (streaming, `--run`, `--dev`, `--output`)

### Internal
- Skill files are embedded as resources in `clibridge4unity.exe` (`<EmbeddedResource Include="skills\*.md">`) — no extra files to ship in the UPM Package, and SETUP can install skills before the UPM import finishes.
- Hash-based overwrite policy: each installed skill ends with `<!-- clibridge4unity:installed-sha=<hex> -->`. On re-install, the existing file's body (everything except the marker line) is rehashed; matching hash → safe to update, mismatch → kept as locally modified, missing marker → kept as user-authored. Detects edits both before AND after the marker line.
- `unity-lint.md` codifies the "don't COMPILE/LINT proactively" rule: `STATUS` first, only escalate when there's evidence of an issue. `COMPILE` is documented as a last resort (breaks pipe via domain reload).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.46 — 2026-05-21

## v1.1.46

### Fixed
- **STATUS no longer times out when Unity's main thread is busy.** Previously, asking for STATUS during an asset import / shader compile / domain reload returned a `TimeoutException` after a long wait. It now degrades gracefully: with a 5s soft deadline on the main-thread dispatch, on timeout it returns `Response.SuccessWithData(...)` with `mainThreadBusy: true`, the busy report (heartbeat staleness, queued commands, open dialogs) embedded as `busyReport`, plus cached values (bridge version, last compile times, compile time stats) from background-safe sources. Same response *type* as the happy path — callers can rely on STATUS to always return something useful.
- **Missing `.meta` file for `BuildCommand.cs`** that caused Unity to log "has no meta file, but it's in an immutable folder. The asset will be ignored" on package install. Added the meta with a fresh GUID.

### Internal
- Refactored `CoreCommands.GetStatus()` from sync into `async Task<string>`; extracted main-thread-only work into `BuildMainThreadStatus()` helper, gated `playModeDuration` on `isPlaying` to avoid stale SessionState leaking after STOP. Moved the profiler marker inside the sync helper so Begin/End don't span an `await`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.45 — 2026-05-18

## v1.1.45

### New
- `SCREENSHOT <uxml> --el <selector>` now force-unhides the target element and every ancestor before rendering: inline `display: Flex`, `visibility: Visible`, `opacity: 1` overrides USS and UXML `style=` attributes. Lets you screenshot elements that production UI keeps hidden until a runtime trigger fires. On-disk UXML is never touched — only the throwaway render clone is mutated.
- Render success message reports `force-unhid N ancestor(s)` so it's obvious the screenshot does not represent the production-runtime state.

### Fixed
- `LINT unity` no longer gets stuck for minutes on big projects. `compilation.Emit()`, `GetDiagnostics()`, and the source-generator driver now all receive a `CancellationToken`, so the wall-clock budget is actually enforceable instead of being silently ignored.
- Added a no-progress watchdog (10s default): if no asmdef completes in 10s, the level is cancelled and partial results are returned. Covers stuck source generators and oversized single asmdefs.
- Lowered the wall-clock budget from 60s to 30s now that cancellation actually works.
- Error message now distinguishes "no asmdef completed in Xms (stuck generator/oversized asmdef)" from "exceeded wall-clock budget" so it's clear which limit fired.
- Zero-size element error in UXML screenshot updated to point at the cases force-unhide can't fix (width:0, position:absolute offscreen, runtime-only data) instead of generic "make it visible".

### Internal
- `LintUnity.Run()` now skips per-asmdef compilation for any user asmdef whose `Library/ScriptAssemblies/<name>.dll` is newer than its `.asmdef` config and every source file. The prebuilt DLL is reused as the downstream MetadataReference. No-change re-runs go from ~30s to sub-second; edits only recompile the touched asmdef + its DAG dependents.
- New `ComputeFreshAsmdefs()` helper centralizes the incremental-skip mtime check; `Format()` now reports `N incremental-skipped` in the mode line so it's visible.
- `LintSourceGenerators.RunGenerators()` accepts and forwards a `CancellationToken` into `CSharpGeneratorDriver.RunGeneratorsAndUpdateCompilation`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.44 — 2026-05-13

## v1.1.44

### Fixed
- Unity-process detection now uses the Windows **Restart Manager** to ask the OS which PIDs hold `<project>/Temp/UnityLockfile` open. Authoritative — works even when multiple projects share the same folder name (e.g. several `test/` projects), or when window titles / command-line paths would otherwise match the wrong instance. The previous window-title and `-projectPath` substring passes are kept as fallbacks for projects that haven't fully booted yet (no lockfile yet).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.43 — 2026-05-13

## v1.1.43

### Fixed
- Unity-process detection now does a normalized-path match pass against `-projectPath` from every running Unity command line. Catches edge cases where the substring `Contains` check failed (trailing slash, mixed slashes, path casing differences, quoted paths) — fixes spurious `state: DifferentProject` when Unity *is* running for the target project.
- `KILL` now prints the expected normalized path + a list of running Unity workspaces (pid + normalized path) when state is `DifferentProject`, instead of the bare "not running" message — makes it obvious why detection didn't match.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.42 — 2026-05-13

## v1.1.42

### New
- `BUILD [--run] [--dev] [--output <path>]` command — builds the Unity Player using the active build target. Streams progress + errors/warnings live over the pipe. `--run` launches Standalone (Win/Mac/Linux) after a successful build; Android/WebGL print install/serve hints. Default output: `Builds/<Target>/<ProductName>`. Other commands auto-block during the build via existing `BuildBypassCommands` allowlist.
- `BridgeCommand.Aliases` — attribute now accepts alternate names that resolve to the same command. HELP renders them inline as `(aliases: X, Y)`. Bypass checks resolve aliases to canonical names so Player-Build / compile-error allowlists behave correctly.
- Command aliases (industry-standard naming, old names still work):
  - `EXEC` → `CODE_EXEC`
  - `EVAL` → `CODE_EXEC_RETURN`
  - `REIMPORT` → `ASSET_RESERIALIZE`

### Removed
- `STACK_MINIMIZE` command — was an internal-only utility never exercised externally. The `StackTraceMinimizer` class is still used internally by `LogCommands` for path shortening; only the public bridge command was dropped.

### Internal
- `SessionKeys.LastBuildPath` / `LastBuildTarget` persist the last build path across domain reloads.
- CLI `hintWorthy` list updated to surface the `LAST` replay hint for `BUILD`, `EXEC`, `EVAL`.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.41 — 2026-05-12

## v1.1.41

### New
- `CODE_EXEC --bg` and `CODE_EXEC_RETURN --bg` — run user code on the threadpool instead of Unity's main thread, with a 25s hard timeout. Use for blocking work (CDP / Chrome DevTools, HTTP requests, large file IO, JSON parsing) so the Editor stays responsive while the call is in flight. On timeout the task is abandoned (still consumes the threadpool slot until it returns) and the bridge returns a clear error — the bridge command slot is no longer held hostage by a stuck blocking call.
- Caveat: Unity API calls from `--bg` code throw "main thread only" exceptions, as expected. Keep Unity API touches on the default (main-thread) path and use `--bg` for pure C# blocking work.

### Why
- Headless-Chrome / CDP scripts run synchronously inside `CODE_EXEC_RETURN` wedge Unity's main thread for seconds-to-minutes, blocking every subsequent bridge command (STATUS, PING, DIAG falling back to busy reports) until the user kills Unity. No safe way to abort code already running on Unity's main thread, so the fix is to keep that work off the main thread entirely.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.40 — 2026-05-12

## v1.1.40

### New
- Custom `[BridgeCommand]` methods now picked up from Unity's default editor catch-all assembly (`Assembly-CSharp-Editor` / `Assembly-CSharp-Editor-firstpass`). Previously the registry only scanned assemblies whose name started with `clibridge4unity` or that explicitly referenced `clibridge4unity.Core`, which forced users to create an asmdef. Now: drop a `.cs` under `Assets/Editor/` with `using clibridge4unity;` and a `[BridgeCommand(...)]` static method — it appears in HELP after the next compile, no asmdef plumbing required.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.39 — 2026-05-11

## v1.1.39

### Fixed
- CLI no longer pops the Win32 "Application Error 0xe0434352" dialog when a background thread (daemon, FileSystemWatcher callback, RoslynDaemon, update-check task) throws an unhandled exception. Now installs `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` handlers at startup, calls `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX)` to suppress Windows Error Reporting, and Main's catch block returns exit code 99 instead of rethrowing. Crash details land in `%TEMP%/clibridge4unity.cli.trace`.
- Bridge no longer duplicates main-thread exceptions to Unity's console. `CommandRegistry.ProcessAllPendingWork` previously called `Debug.LogError` AND surfaced the exception via `TaskCompletionSource.TrySetException` — caller already gets the exception in the command response, so the Debug.LogError was pure noise (it also got captured by the per-command log capture, doubling reports). Diagnostic file log retained.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.38 — 2026-05-11

## v1.1.38

### Internal — STATUS perf overhaul
- **Removed full-tree mtime scan from `ScanModifiedScripts`** — previously walked every `.cs/.asmdef/.asmref` under `Assets/`, `Packages/`, and every local `file:` package root on every cache miss (was 870ms + 8.8MB GC alloc per call on a big project). Daemon's FileSystemWatcher is authoritative; no need to re-walk.
- **Removed `GetLocalFilePackageRoots` helper** — only existed to feed the deleted mtime scan.
- **STATUS skips `LogCommands.GetCompileErrorsFromConsole()` when console has 0 errors** — was always-on, even when nothing to find.
- **STATUS computes `ScriptsModifiedSinceCompileCached()` once and reuses 3×** — was called separately for each of `scriptsModified`, `compileRecommended`, and `compileRecommendation`.
- **`ScriptsModifiedSinceCompileCached` TTL bumped 1s → 10s** — daemon catches changes in real time, so STATUS doesn't need 1-second freshness.
- **Dropped per-command file-I/O log spam in `CommandRegistry`** — `BridgeDiagnostics.Log` was called on every main-thread work begin/end/queued (3× `File.AppendAllText` per command). Startup/error logs kept; per-work-item noise removed.

Net effect: STATUS now does roughly zero work on the steady-state path (daemon log present, console clean, cache fresh).

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.37 — 2026-05-11

## v1.1.37

### Internal
- More granular Profiler markers inside STATUS to find why a single STATUS tick can spike to ~880ms with 8.8MB GC alloc on big projects. New sub-markers nested under `Bridge.Core.Status`:
  - `Bridge.Core.Status.OpenEditorWindows` — only fires on cache miss (`Resources.FindObjectsOfTypeAll<EditorWindow>()`)
  - `Bridge.Core.Status.ScriptsModifiedSinceCompile` — only on cache miss (file mtime walk)
  - `Bridge.Core.Status.GetCompileTimeStats` — `BridgeServer.GetCompileTimeStats()`
  - `Bridge.Core.Status.GetCompileErrors` — `CommandRegistry.GetCompileErrors()` callback + Regex match
- New markers in `LogCommands` for the Unity-internal `LogEntries` reflection helpers that STATUS funnels through:
  - `Bridge.Core.GetConsoleCounts`
  - `Bridge.Core.GetCompileErrorsFromConsole` — full console iteration
  - `Bridge.Core.GetUiToolkitDiagnosticsFromConsole` — full console iteration + per-asset import-error fetch
- After this release, run STATUS once with the Unity Profiler open and the slow sub-call inside STATUS will be self-evident in the hierarchy.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.36 — 2026-05-11

## v1.1.36

### Internal
- Profiler markers added across all bridge commands and heavy helper methods. Open Unity Profiler → Hierarchy and search "Bridge." to see exactly where time goes during a slow command.
  - Central wrap in `CommandRegistry.InvokeCommand` emits `Bridge.{COMMAND_NAME}` for every dispatched command. No per-command opt-in needed.
  - Per-method `ProfilerMarker.Auto()` scopes added to the heaviest paths:
    - `RenderCommand`: Render, RenderUxml, RenderUxmlSettlePump, RenderGrid, RenderPrefab, RenderUIPrefab, Render3DPrefab, RenderGameObject
    - `UICommands` (ASSET_DISCOVER): DiscoverSummary, DiscoverUI, DiscoverShaders, DiscoverMaterials, DiscoverSprites, DiscoverUIPrefabs, DiscoverScenes, DiscoverFonts, DiscoverModels, DiscoverVariants
    - `AssetSearch`: Search, FindPrefabsWithComponent, FindMaterialsWithShader, FindAssetsWithLabel, FindScriptsInheriting, FindAssetsOfType, GetDependencies, FindReferences
    - `AssetManagement`: Move, Copy, Delete, Mkdir, Label, Reserialize
    - `ComponentCommands`: Inspector, FindType (full AppDomain type sweep), Set, Add, Remove
    - `CodeExecutor`: Execute, ExecuteReturn, Compile, CompileRoslyn, CompileMcs, CollectMcsReferences
    - `PrefabCommands`: Create, Save, Instantiate, Apply, Unpack, List
    - `SceneCommands`: Create, Find, Delete, Save, Load, SceneView
    - `CoreCommands`: Status, Compile, Refresh, Menu, Profile, Diag
    - `LogCommands`: GetLogs, GetCompileErrorsSummary, GetUiToolkitDiagnosticsForCommand, EndCommandCapture
- Markers use struct-based `Unity.Profiling.ProfilerMarker` (zero allocation) and only register the scope when Unity's deep profiler / `Bridge.*` filter is active, so there is no measurable overhead in normal use.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.35 — 2026-05-10

## v1.1.35

### Fixed
- `SCREENSHOT Assets/Foo.uxml` no longer flashes a popup window onscreen and no longer wakes a backgrounded Unity to the foreground. Setting `EditorWindow.position` to negative coords doesn't work because Unity's `ContainerWindow` clamps the position to the nearest monitor — so the popup ended up onscreen, and `ShowPopup` woke Unity's message pump to paint it. Fix: after `ShowPopup`, walk reflection chain `EditorWindow → m_Parent → window → windowPtr` to grab the popup's HWND, then call Win32 `SetWindowPos` to `(-30000, -30000)` with `SWP_NOACTIVATE`. Win32 doesn't clamp arbitrary HWND moves, and `SWP_NOACTIVATE` keeps Unity in the background.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.34 — 2026-05-10

## v1.1.34

### New
- `SAVE Assets/Path/Foo.unity` — explicit path argument writes the active scene directly via `EditorSceneManager.SaveScene`. Bypasses the native "Save Scene As..." file dialog that `SaveOpenScenes` opens for Untitled scenes (and that wedges the bridge because CLI can't dismiss native file dialogs).
- `GAMEVIEW WxH` now sets the resolution via Unity's internal `GameViewSizes` API (adds/selects a `clibridge_WxH` entry in the size dropdown). Docked Game tab stays docked instead of being torn off into a floating window. Falls back to the old window-resize path if the reflection chain breaks (with the failure reason in the response).
- `SCREENSHOT Assets/Foo.uxml` — viewport size now inferred from the UXML root child's declared `style.width`/`style.height` (pixel units). Layouts authored for a specific resolution render at intended size; falls back to 1920x1080.
- `SCREENSHOT Assets/Foo.uxml --el <selector>` — auto-supersamples small elements: after layout settles, if the element's max dimension is under 600px the root tree is rescaled (up to 8x) and re-rendered before crop. Avoids tiny blurry thumbnails like 168x9 for `.checkbox-group`.

### Fixed
- `SAVE` on an Untitled scene no longer opens a native Save dialog that blocks Unity's main thread. Returns a clear error pointing to the `SAVE <path>` form instead.
- `SCREENSHOT gameview` no longer steals focus from a docked tab. Removed the `gameView.Focus()` call — `Repaint` + `RepaintImmediately` is sufficient to refresh the backing buffer.
- `SCREENSHOT Assets/Foo.uxml` — render window position now set BEFORE `ShowPopup` so the offscreen popup never flashes at the default location for one frame.
- `SCREENSHOT Assets/Foo.uxml` — multi-pass repaint pump (RepaintImmediately + MarkDirtyRepaint, 50ms gap, 6 passes) before final GrabPixels so dynamic font atlas / TMP measure-then-relayout cycles settle and text positions stabilize.

### Internal
- `CheckStuckDomainReload` (DIAG): now gathers progress evidence before recommending KILL+OPEN. Samples Editor.log mtime, Unity process CPU% over a 1.2s window, and Win32 progress-bar movement (PBM_GETPOS sent cross-process to msctls_progress32 children of any open dialog). Verdict is "still working" if any signal is active, "appears frozen" only when all three are quiet — then suggests DIAG → WAKEUP → KILL in order.
- `CheckStuckDomainReload`: adaptive threshold for `compiling`/`reloading` states — uses `max(60, max(180, compileAvg * 3))` so a normal 90-180s compile on a big project no longer triggers stuck warnings. `importing` keeps the lower 60s threshold.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.33 — 2026-05-10

## v1.1.33

### New
- `SCREENSHOT Assets/Foo.uxml` now infers viewport size from the UXML root element's declared `style.width`/`style.height` (pixel units). Layouts authored for a specific resolution (e.g. 1920×1080 panel) render at their intended size instead of being forced into an 800×450 box that collapsed flex children. Falls back to 1920×1080 when no pixel size is declared.
- `SCREENSHOT Assets/Foo.uxml --el <selector>` auto-supersamples small elements: after layout settles, if the element's max dimension is under 600px the root tree is rescaled (up to 8×) and re-rendered before crop. Tiny elements like `.checkbox-group` come out as crisp larger images instead of blurry 168×9 thumbnails.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.32 — 2026-05-09

## v1.1.32

### Fixed
- `SCREENSHOT Assets/Foo.uxml`: text positions sometimes captured mid-layout (before dynamic font atlas / TMP measure-then-relayout cycle settled). Added 6-pass repaint pump (RepaintImmediately + MarkDirtyRepaint, 50ms gap) before final GrabPixels so scheduled callbacks, bindings, and GeometryChangedEvent handlers run and text positions stabilize.

---
Install: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.31 — 2026-05-09

### Files changed
```
.../scripts/__pycache__/ui_analyze.cpython-310.pyc | Bin 39055 -> 0 bytes
 .claude/scripts/deploy.py                          | 141 +++-
 .gitignore                                         |   9 +
 CLAUDE.md                                          |   4 +-
 ConsoleUnityBridge.sln                             |  25 +
 Package/CLAUDE.md                                  |   2 +-
 Package/Editor/Commands/Asset/AssetManagement.cs   |  10 +-
 Package/Editor/Commands/Scene/PlayModeCommands.cs  | 173 ++++-
 Package/Editor/Commands/Scene/SceneCommands.cs     |  37 +-
 Package/Editor/Core/BridgeServer.cs                |  17 +-
 Package/Editor/Core/CommandRegistry.cs             |  69 +-
 Package/Editor/Core/SetupWizard.cs                 |  52 +-
 Package/package.json                               |   2 +-
 SUMMARY.md                                         |   2 +-
 clibridge4unity/CodeAnalysisCore.cs                | 140 +++-
 clibridge4unity/LintAsmdef.cs                      | 271 ++++++++
 clibridge4unity/LintCscRsp.cs                      | 134 ++++
 clibridge4unity/LintSemantic.cs                    | 365 ++++++++--
 clibridge4unity/LintSourceGenerators.cs            | 157 +++++
 clibridge4unity/LintUnity.cs                       | 763 +++++++++++++++++++++
 clibridge4unity/ReportServer.cs                    | 184 +++--
 clibridge4unity/RoslynDaemon.cs                    |  92 ++-
 clibridge4unity/clibridge4unity.cs                 | 402 ++++++++---
 clibridge4unity/clibridge4unity.csproj             |   2 +-
 install.ps1                                        |   2 +-
 pyproject.toml                                     |   4 +
 tests/conftest.py                                  |  50 ++
 tests/test_asset.py                                |  27 +
 tests/test_code.py                                 |  59 ++
 tests/test_component.py                            |  65 ++
 tests/test_core.py                                 | 131 ++++
 tests/test_errors.py                               |  37 +
 tests/test_playmode.py                             |  27 +
 tests/test_prefab.py                               |  37 +
 tests/test_scene.py                                |  71 ++
 tests/test_ui.py                                   |  37 +
 36 files changed, 3252 insertions(+), 348 deletions(-)
```

**Diff:** 37 files changed, 3252 insertions(+), 348 deletions(-)

**Full comparison:** https://github.com/oddgames/clibridge4unity/compare/v1.1.30...v1.1.31

**Install:** `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

## v1.1.30 — 2026-05-09

**Full comparison:** https://github.com/oddgames/clibridge4unity/compare/v1.1.29...v1.1.30

**Install:** `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`

