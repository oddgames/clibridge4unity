---
name: clibridge4unity-device
description: Get a build onto a phone and drive it. `DEVICE install <apk|ipa>` sideloads over USB (adb / libimobiledevice), `DEVICE screenshot|ui|tap|type|key` sees and taps NATIVE Android UI (permission dialogs, consent sheets), `BUGPUNCH <device> …` runs C#, taps, screenshots, reads console/hierarchy and pulls memory snapshots on a device running the Bugpunch SDK (server API, INTERNAL-role devices only), and `DEVICE script` runs a file of those steps into a markdown report. Every command ends with an [artifacts] line naming the files it wrote. Auto-trigger on "install on the phone", "put this build on my device", "adb install", "sideload the ipa", "run this on the device", "tap the button on the phone", "screenshot the device", "what's in the device log", "device hierarchy", or any test that must happen on real hardware rather than in the editor.
---

# Install on a device, then drive it

Two commands, two transports. Neither needs Unity or a project directory.

| | `DEVICE` | `BUGPUNCH` |
|---|---|---|
| Path | USB cable — adb (Android), libimobiledevice (iOS) | Bugpunch server → the SDK inside the running game |
| Sees | whatever is plugged in | whatever is signed in with an **internal** tester |
| For | install / uninstall / launch / OS log | run C#, tap, swipe, screenshot, console, hierarchy, PlayerPrefs |

## 1. Put the build on the phone

```bash
clibridge4unity DEVICE list                       # Android + iOS, with state (unauthorized = accept the prompt on the phone)
clibridge4unity DEVICE install Builds/Android/game.apk --launch
clibridge4unity DEVICE install Builds/iOS/game.ipa --launch
clibridge4unity DEVICE install game.apk --serial 1A2B     # more than one device plugged in → pick one (prefix ok)
clibridge4unity DEVICE log --lines 300            # Unity-tagged logcat / syslog; --follow streams, --all drops the filter
clibridge4unity DEVICE uninstall com.foo.game
```
- Extension decides the platform. `.aab` is refused (bundletool or build an apk).
- `--launch` needs the package/bundle id: read from the apk via aapt or the ipa's Info.plist; failing that, inferred by diffing installed packages. Pass `--package <id>` when it says it can't tell.
- iOS launch attaches `idevicedebug` and leaves it running — closing that process kills the app. Tapping the icon on the phone is the alternative.
- **iOS and the Developer Disk Image.** Live screenshot + USB launch need the DDI. On iOS <= 16 the CLI fetches and mounts it automatically (same GitHub image set Ipaapk uses, cloned once to `~/.clibridge4unity/ddi`). On iOS 17+ the DDI is personalized and libimobiledevice can't mount it, so: launch = tap the icon (or Bugpunch), and `DEVICE screenshot` falls back to **the phone's own screenshot pulled from the camera roll over AFC** (Ipaapk's `afcshots`): `--roll` grabs the latest, `--wait N` waits for one the tester takes now (Side + Volume Up). Everything interactive on a modern iPhone goes through Bugpunch — `DEVICE up` still installs, captures syslog and links it.
- **adb server safety:** the CLI runs the *same adb binary* as any adb server already running (Ipaapk, Unity), then Ipaapk's own adb, then SDKs. A different build would kill the shared server and drop everyone else's device list. It never issues `kill-server`. `DEVICE tools` shows what it picked.

## 2. Drive it through Bugpunch

One-time: create a token at `<server>/settings` → API Keys with **Device control (admin)** ticked (org admins only), then
```bash
clibridge4unity BUGPUNCH auth bugpunch_pat_…        # verifies, stores ~/.clibridge4unity/bugpunch.json (BUGPUNCH_TOKEN / BUGPUNCH_SERVER env override)
```
Then:
```bash
clibridge4unity BUGPUNCH devices [--all]            # internal devices; --all adds recently-seen offline ones
clibridge4unity BUGPUNCH pixel run @probe.cs        # <device> = id, id prefix, or name/nickname/model substring
clibridge4unity BUGPUNCH pixel run "return UnityEngine.Application.version;"
clibridge4unity BUGPUNCH pixel screenshot [label]   # → session dir (see section 4); --out to choose
clibridge4unity BUGPUNCH pixel tap 0.5 0.85         # normalised, origin top-left
clibridge4unity BUGPUNCH pixel swipe 0.8 0.5 0.2 0.5 400
clibridge4unity BUGPUNCH pixel log                  # console since logId 0 (pass the last id to page)
clibridge4unity BUGPUNCH pixel hierarchy | info | perf | prefs
clibridge4unity BUGPUNCH pixel action '{"action":"click","target":"PlayButton"}'
clibridge4unity BUGPUNCH pixel get /children?id=12  # any Remote-IDE RPC path; post <path> [json|@file]
```
- **Only internal devices, ever.** The server filters by the signed-in tester's role; external/alpha/public testers' phones don't list and 404 on control. No flag widens it.
- Exit codes: `0` ok · `1` refused/failed (403 = read-only token or another dashboard user holds the controller lock) · `10` device offline / not found · `14` timeout.
- `run` returns the script's output; a compile error comes back as the body — fix and re-run. Needs a build with the scripting runtime (release builds of the test app have it; a stripped player may 501).

## 3. Native UI — what Bugpunch can't see or touch (Android over the cable)

Permission dialogs, the SDK's consent sheet / crash overlay, IAP sheets, the keyboard: all native, all invisible to `BUGPUNCH screenshot` and deaf to `BUGPUNCH tap`.
```bash
clibridge4unity DEVICE screenshot [label]        # the real screen, native views included (iOS too, once the Developer Disk Image is mounted)
clibridge4unity DEVICE ui [filter]               # uiautomator view tree: center, clickable, text/desc, resource-id
clibridge4unity DEVICE tap "Allow"               # by text / content-desc / resource-id (exact first, then substring, clickable preferred)
clibridge4unity DEVICE tap 540 1820              # by pixel
clibridge4unity DEVICE swipe 900 1200 100 1200 300
clibridge4unity DEVICE type "player one" ; DEVICE key ENTER ; DEVICE key BACK
```
iOS over USB is **see-only** — tapping native iOS UI needs WebDriverAgent from a Mac, which libimobiledevice can't do. Inside the Unity view, `BUGPUNCH tap` works on both.

## 4. Where everything lands

Every DEVICE / BUGPUNCH command ends with one stdout line:
```
[artifacts] dir=~\.clibridge4unity\devices\<serial|id>  screenshot=…\0007_screenshot_after-login.png  logcat=…\logcat.txt (+42 lines)  actions=…\actions.log
```
Read that line, not the prose — it names every file the command wrote. Artifacts are numbered (`0007_ui_Allow.txt`, `0012_run_probe.txt`, `0013_memsnap_….snap`), `actions.log` is the device's command history, and `logcat.txt` / `syslog.txt` is a **background capture** (`DEVICE log --start`, auto-started by `launch` / `install --launch` / `script`; `--stop` ends it). The `(+N lines)` is how much arrived since the previous command — "did that tap do anything" without opening the file. `DEVICE log` tails the capture while it runs.

## 5. Scripts — the automation

`DEVICE script steps.txt [--bp <device>] [--continue] [--shots]` runs one command per line, cable and Bugpunch steps mixed, and writes a markdown report (step table + every screenshot inline) into the session dir:
```
install Builds/game.apk --launch
waitfor "Allow" 30          # poll native UI until it appears (waitfor-not for the opposite)
tap "Allow"
bp waitonline 90            # wait for the Bugpunch tunnel; fixes <device> for the bp lines below
bp run @scripts/skip-tutorial.cs
bp tap 0.5 0.85
wait 2
bp screenshot main-menu
bp assert "MainMenu"        # Unity hierarchy must contain it
assert-not "Allow"          # native UI must not
bp memsnap
```
Unprefixed = DEVICE, `bp` = BUGPUNCH. Stops at the first failure (`--continue` to keep going); `--shots` screenshots after every step; a failed `assert`/`waitfor` screenshots the screen it saw. Exit 1 if any step failed; the report path is in the footer.

## 6. Memory snapshots

`BUGPUNCH <dev> memsnap` takes a Unity Memory Profiler capture **on the device** (Development Build required), waits for it, pulls it here in 1 MB chunks (resumable — a whole .snap as one frame is what used to die in the tunnel), then runs `MEMSNAP summary` on it. `memsnap list` / `pull <devicePath>` / `delete <devicePath>` manage what's on the phone (budget: 6 captures / 2 GB). Diff two pulls with `MEMSNAP a.snap b.snap`.

## 0. The one call

```bash
clibridge4unity DEVICE up Builds/game.apk            # install → launch → log capture → link the Bugpunch device → remember it
clibridge4unity DEVICE up game.ipa --bp "iPhone12,3"  # name the Bugpunch device if the model match is ambiguous
clibridge4unity DEVICE up --no-install --package com.foo.game   # re-use what's on the phone
```
**Route.** `up` first tries the **direct route**: a Development Build serves the Remote IDE API itself (SDK `LocalIdeServer`, port 47701), and `up` brings it to `127.0.0.1` over USB (`adb forward` / `iproxy`) — no Bugpunch server, no token, no tester sign-in. `--lan <ip>` uses the network instead; `DEVICE discover` lists dev builds advertising on the LAN. Only when nothing answers (release build, SDK < 0.8.223, phone elsewhere) does it fall back to the Bugpunch server, which needs the token. `BUGPUNCH http://127.0.0.1:PORT <verb>` works on a URL directly too.

After `up`, no command needs `--serial` or a device name: `DEVICE screenshot`, `DEVICE tap "Allow"`, `DEVICE ui` hit the cable; `DEVICE run "<C#>"`, `DEVICE hierarchy`, `DEVICE memsnap`, `DEVICE bp tap 0.5 0.8` go through the linked Bugpunch device. `DEVICE status` shows the session, `DEVICE shell` is a REPL over the same words (also reads piped stdin), `DEVICE script` inherits the link, `DEVICE down [--uninstall]` ends it. State lives in `session.json` in the session dir. Sections 1–6 are the primitives it composes.

## Typical loop

`BUILD` (or the project's build skill) → `DEVICE install … --launch` → wait for `BUGPUNCH devices` to show it online → `BUGPUNCH <dev> screenshot` to see where it is → `tap` / `run` → `log` for what happened. Pair `DEVICE log --follow` in a second terminal when the game itself is what's crashing (Bugpunch can't answer once the process is gone).
