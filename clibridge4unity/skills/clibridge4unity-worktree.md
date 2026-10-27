---
name: clibridge4unity-worktree
description: Start a parallel task on another branch in its own checkout (a worktree) without touching this checkout or its Unity editor, and work correctly inside one. `WORKTREE create <branch>` makes a Plastic cloaked workspace or a git worktree beside the project; Claude Code's EnterWorktree / `claude -w` go through it once SETUP has registered the hooks. Inside a worktree the bridge refuses editor commands and lints against the origin's Library. Use when the user says "worktree", "work on <branch> in parallel", "start a second task on another branch", "EnterWorktree", or when a command answers "This is a worktree copy … of <origin>".
---

# Worktrees: a second checkout per task

One Unity editor serves one checkout. A worktree is another checkout of the same project on another
branch, made beside it, so a second agent can edit, lint and analyse there while this checkout keeps
the editor. The bridge treats the copy as a project **without an editor**: nothing will ever serve
its pipe, so it says so at once instead of waiting.

## 1. Before creating one — always ask about the branch

- The worktree **name is the branch** (`main/carshow_fix` loads `/main/carshow_fix` on Plastic, the
  branch `main/carshow_fix` on git). Use the branch the user gave; never infer one from the task.
  Segments may hold only letters, digits, `.`, `_`, `-`, joined by `/`, at most 64 characters.
- **The branch must already exist.** `WORKTREE create` never creates one on a shared repository. If
  it is missing, offer to create it, name its parent, and do it only on a yes
  (Plastic: `cm branch create /main/x`, from Git Bash with `MSYS_NO_PATHCONV=1`; git: `git branch x`).
- **Does the task need the Unity editor?** Seeing or running prefabs, menus and scenes, play mode,
  tests, screenshots and imports all need one, and a cloaked copy has neither an editor nor the
  textures, models and audio. Ask before creating anything. If it does, offer adopting a full
  workspace on the branch (`--adopt`, below) or doing the work in this checkout once it is on the
  branch. A cloaked copy is for code, YAML-level prefab and scene edits, lint and analysis.
- A session holds one worktree. If the working directory already contains
  `.clibridge4unity/worktree.json`, this is a worktree: a second parallel task gets its own tab.
- Creation downloads the branch: minutes, and a GB or two for a large project. Say so first.
- **Never switch an existing full workspace to the branch without asking, every time, naming it.**
  Some workspaces are thousands of changesets behind; switching one can mean hours of download plus
  a Unity reimport. Adopting a full workspace (`--adopt <path>`) is for work that needs an editor on
  that branch; offer it, never choose it yourself, and never switch one that has pending changes or
  a Unity editor open on it.

## 2. Create and enter

- From a Claude session: `EnterWorktree` with `name` = the branch without its leading `/`. Claude
  runs the registered `clibridge4unity HOOK worktree-create`, which creates (or reuses) the checkout
  and moves the session into it. If it fails, report the hook's message and stop; never retry with a
  different or generated name.
- From a terminal: `clibridge4unity WORKTREE create <branch>` prints the project path; enter it with
  `EnterWorktree` on the same name (the hook reuses the checkout) or `claude -w <name>`.
- `WORKTREE list` shows the copies beside this project with their branch and kind.

What you get:
- **Plastic:** a regular workspace `<prefix><slug>` at `<root>/<slug>` (`<slug>` = branch with `/` as
  `--`) with a `cloaked.conf` written before the first update, so images, models, audio, archives,
  native plugins and the project's configured heavy folders never download; code, prefabs, scenes,
  configs and every `.meta` do. A branch whose project folder or root contains a `.git` is refused:
  Claude Code refuses a worktree whose `.git` it cannot verify.
- **git:** `git worktree add` of the existing branch (a local one, or `origin/<branch>` tracked).
  Claude Code's own git worktrees are recognised too, without the hooks or a marker.

## 3. Working inside a worktree

- Editor commands (`STATUS`, `COMPILE`, `PLAY`, `OPEN`, `WAKEUP`, `DISMISS`, `READY`, `PING`,
  `SCREENSHOT`, …) exit 10 at once with "This is a worktree copy … of <origin>". Do not retry them,
  do not run `OPEN` (`OPEN force` imports the whole project into the copy and is almost never
  wanted), and do not run them against the origin either: the origin's editor is on its own branch
  and belongs to whoever launched it.
- If the task turns out to need the editor after all, the first such command is that refusal. Stop
  there and tell the user; the choice is theirs: switch the origin to this branch (ask, every
  time), adopt a full workspace, or finish the code part here and test from the origin after it
  is checked in.
- `LINT`, `LINT unity`, `ANALYZE` and `MAP` work. `LINT unity` compiles this copy's sources against
  the **origin's** `Library` (ScriptAssemblies, packages) and says so in its header; it is as current
  as the origin's last compile, it recompiles every user asmdef (no "DLL up-to-date" skip), and it
  warns when this copy's `Packages/manifest.json` differs from the origin's.
- Cloaked files are absent in a Plastic worktree, so anything that needs textures, models or audio
  (screenshots, imports, play mode) needs a full checkout.
- The index daemon starts on demand for `ANALYZE`/`MAP`/`LINT` and is this copy's own; nothing here
  reaches the origin's editor or daemon.
- The Bash tool refuses long command chains inside a hook-made worktree ("too complex to verify");
  use plain single commands.
- Don't check in or commit from the worktree unless the project's rules allow it; don't call
  `ExitWorktree` unless the user asks.

## 4. Finishing

- Check in / commit from the worktree's checkout as the project's rules say (Plastic: the pending
  changes in that workspace; git: the worktree's branch).
- Leaving: ask the user whether to **keep** or **remove**. Keep leaves the checkout; entering the
  same name later reuses it. Remove deletes it only once it has no pending changes and no new files
  that aren't added or committed; otherwise it refuses and lists them. Branches are never deleted.
- Claude Code cannot verify a hook-made worktree itself, so `ExitWorktree remove` first asks for
  `discard_changes: true`; that only lets the hook run, and the hook's guard above is what protects
  unfinished work. In VS Code, ask Claude to exit the worktree before closing the tab; closing the
  tab does not remove it.
- An adopted full workspace is only detached on remove and stays on its branch.

## Configuration (`ProjectSettings/clibridge4unity.json`, versioned)

```json
{
  "worktree": {
    "root": "../my_worktrees",
    "namePrefix": "wt_",
    "cloaked": ["/UnityProj/Assets/Terrain", "/UnityProj/Assets/Big Data/*.asset"],
    "cloakedDefaults": true,
    "hooks": true
  }
}
```
`root` is where copies go (relative to the checkout root; default a `<checkout>_worktrees` sibling).
`namePrefix` prefixes Plastic workspace names. `cloaked` adds rules (ignore.conf syntax, paths from
the checkout root) to the built-in set of image, model, audio, video, archive and native-plugin
extensions; `cloakedDefaults: false` drops the built-in set. `hooks` decides whether SETUP registers
`WorktreeCreate`/`WorktreeRemove` in `.claude/settings.json`: default on for Plastic, off for git,
where Claude's own worktrees already work.
