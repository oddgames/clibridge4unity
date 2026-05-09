#!/usr/bin/env python
"""Deploy clibridge4unity: build, package, push, tag, release, upload, verify."""
import sys, os, subprocess, shutil, tempfile, time, re, shlex, zipfile

def format_cmd(args):
    return " ".join(shlex.quote(str(a)) for a in args)

def run(args, check=True, capture=False, timeout=120, cwd=None):
    """Run a shell command, print it, return result."""
    print(f"  $ {format_cmd(args)}")
    r = subprocess.run(args, shell=False, capture_output=capture, text=True, timeout=timeout, cwd=cwd)
    if check and r.returncode != 0:
        if capture:
            print(r.stdout)
            print(r.stderr, file=sys.stderr)
        print(f"FAILED: exit {r.returncode}", file=sys.stderr)
        sys.exit(1)
    return r


def build_release_notes(tag, version, root):
    """Build a markdown notes body for the release.

    Preference order:
      1. If `.claude/RELEASE_NOTES.md` exists, use its content verbatim (a fresh per-release
         note the agent/human filled in before deploying — cleared after use).
      2. Otherwise synthesise from `git log <prev_tag>..HEAD` + a file-change summary.
    """
    notes_file = os.path.join(root, ".claude", "RELEASE_NOTES.md")
    if os.path.isfile(notes_file):
        with open(notes_file, "r", encoding="utf-8") as f:
            body = f.read().strip()
        # Blank the file so the next release doesn't reuse stale notes.
        try:
            with open(notes_file, "w", encoding="utf-8") as f:
                f.write("")
        except Exception:
            pass
        if body:
            return body + f"\n\n---\nInstall: `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`"
    # Loud warning so the agent knows synthesised notes are coming and can fix them.
    print("  WARNING: .claude/RELEASE_NOTES.md is empty — synthesising notes from git diff stats.", file=sys.stderr)
    print("           For human-readable changelogs, write a New/Fixed summary to that file before /deploy.", file=sys.stderr)

    # Synthesise from git: commits since previous tag + file-change shortstat. We run BEFORE
    # the "Release vX.Y.Z" commit is created — so we also need to surface the working-tree
    # changes (staged + unstaged) that are about to be rolled into that release commit. Without
    # this, releases done in a single agent session (no intermediate commits) end up with an
    # empty notes body because `prev..HEAD` finds zero new commits.
    prev = subprocess.run(
        ["git", "describe", "--tags", "--abbrev=0", "--match", "v*", "HEAD"],
        shell=False, capture_output=True, text=True,
    ).stdout.strip()

    lines = []
    if prev:
        # 1) Real commits since the previous tag (excluding any prior `Release` commits).
        r = subprocess.run(
            ["git", "log", f"{prev}..HEAD", "--pretty=format:%s"],
            shell=False, capture_output=True, text=True,
        )
        commits = [l.strip() for l in r.stdout.split("\n") if l.strip()]
        commits = [c for c in commits if not c.startswith(f"Release {tag}") and not c.startswith("Release v")]
        if commits:
            lines.append("### Commits")
            for c in commits:
                lines.append(f"- {c}")
            lines.append("")

        # 2) About-to-be-committed working-tree changes (staged + unstaged, vs prev tag) —
        #    captures everything the agent edited in this session before /deploy ran.
        r = subprocess.run(
            ["git", "diff", "--stat", prev, "--", ".",
             ":(exclude)Package/Tools/win-x64/clibridge4unity.exe",
             ":(exclude)CHANGELOG.md", ":(exclude).claude/RELEASE_NOTES.md"],
            shell=False, capture_output=True, text=True,
        )
        diff_stat = r.stdout.strip()
        if diff_stat:
            lines.append("### Files changed")
            lines.append("```")
            lines.append(diff_stat)
            lines.append("```")
            lines.append("")

        # 3) Short summary line.
        r = subprocess.run(
            ["git", "diff", "--shortstat", prev],
            shell=False, capture_output=True, text=True,
        )
        if r.stdout.strip():
            lines.append(f"**Diff:** {r.stdout.strip()}")
            lines.append("")

        lines.append(f"**Full comparison:** https://github.com/oddgames/clibridge4unity/compare/{prev}...{tag}")
        lines.append("")

    lines.append(f"**Install:** `irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex`")
    return "\n".join(lines).strip()


def prepend_changelog(root, tag, version, notes_body):
    """Prepend a new release entry to CHANGELOG.md so the repo always carries an in-tree
    history of what shipped each version. Creates the file if missing."""
    import datetime
    changelog = os.path.join(root, "CHANGELOG.md")
    today = datetime.date.today().isoformat()
    header = f"## {tag} — {today}\n\n"
    body = (notes_body.strip() + "\n") if notes_body else ""
    new_entry = header + body + "\n"

    if os.path.isfile(changelog):
        with open(changelog, "r", encoding="utf-8") as f:
            existing = f.read()
        # Skip if this tag already in the file (re-runs of /deploy build).
        if f"## {tag} —" in existing:
            return
        # Preserve top-of-file title if present.
        if existing.startswith("# "):
            nl = existing.find("\n\n")
            if nl > 0:
                title = existing[: nl + 2]
                rest = existing[nl + 2 :]
                with open(changelog, "w", encoding="utf-8") as f:
                    f.write(title + new_entry + rest)
                return
        with open(changelog, "w", encoding="utf-8") as f:
            f.write(new_entry + existing)
        return

    with open(changelog, "w", encoding="utf-8") as f:
        f.write("# Changelog\n\n" + new_entry)

def main():
    if len(sys.argv) < 2:
        print("Usage: deploy.py <version>", file=sys.stderr)
        sys.exit(1)

    version = sys.argv[1]
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", version):
        print(f"ERROR: invalid version: {version!r}", file=sys.stderr)
        sys.exit(1)

    tag = f"v{version}"
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], shell=False, capture_output=True, text=True).stdout.strip()
    if not root:
        print("ERROR: not in a git repo", file=sys.stderr)
        sys.exit(1)
    os.chdir(root)

    exe_path = os.path.join(root, "clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe")
    package_exe = os.path.join(root, "Package", "Tools", "win-x64", "clibridge4unity.exe")

    # Step 1: Build
    print(f"\n=== Building v{version} ===")
    cli_project = os.path.join(root, "clibridge4unity")
    run(["dotnet", "clean", "-c", "Release", "-v", "q"], timeout=180, cwd=cli_project)
    run(["dotnet", "publish", "-c", "Release"], timeout=180, cwd=cli_project)
    if not os.path.isfile(exe_path):
        print(f"ERROR: exe not found at {exe_path}", file=sys.stderr)
        sys.exit(1)
    size_mb = os.path.getsize(exe_path) / 1024 / 1024
    print(f"  Built: {exe_path} ({size_mb:.1f} MB)")

    # Step 2: Verify version
    print(f"\n=== Verifying version ===")
    r = run([exe_path, "--version"], capture=True)
    if version not in r.stdout:
        print(f"ERROR: exe reports '{r.stdout.strip()}', expected {version}", file=sys.stderr)
        sys.exit(1)
    print(f"  OK: {r.stdout.strip()}")

    # Step 3: Package
    print(f"\n=== Packaging ===")
    zip_path = os.path.join(tempfile.gettempdir(), "clibridge4unity-win-x64.zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(exe_path, "clibridge4unity.exe")
    print(f"  Created: {zip_path}")

    os.makedirs(os.path.dirname(package_exe), exist_ok=True)
    shutil.copy2(exe_path, package_exe)
    print(f"  Updated package binary: {package_exe}")

    # Build release notes BEFORE committing — they go into CHANGELOG.md, the commit, and `gh release`.
    notes_body = build_release_notes(tag, version, root)
    prepend_changelog(root, tag, version, notes_body)

    # Step 4: Git commit and push. Use `git add -A` so EVERY working-tree change rolls into
    # the Release commit — earlier deploys narrow-staged the version-bump files only and
    # silently dropped all source edits made during the same agent session, leaving the
    # GitHub source out of sync with the bundled exe.
    print(f"\n=== Git commit and push ===")
    run(["git", "add", "-A"])
    # Allow the commit to be empty (idempotent re-runs of /deploy build): exit 0 on no-op.
    r = run(["git", "diff", "--cached", "--quiet"], check=False)
    if r.returncode == 0:
        print("  (no changes to commit)")
    else:
        run(["git", "commit", "-m", f"Release {tag}"])
        run(["git", "push", "origin", "main"], timeout=180)

    # Step 5: Tag and release
    print(f"\n=== Creating tag and release ===")
    run(["git", "tag", tag], check=False)
    run(["git", "push", "origin", tag], timeout=180, check=False)

    notes_tmp = os.path.join(tempfile.gettempdir(), f"release_notes_{tag}.md")
    with open(notes_tmp, "w", encoding="utf-8") as f:
        f.write(notes_body)
    run(["gh", "release", "create", tag, "--title", tag, "--notes-file", notes_tmp], check=False)

    # Step 6: Upload assets
    print(f"\n=== Uploading assets ===")
    run(["gh", "release", "upload", tag, zip_path, "--clobber"], timeout=180)
    run(["gh", "release", "upload", tag, exe_path, "--clobber"], timeout=180)

    # Step 7: Verify
    print(f"\n=== Verifying release ===")
    r = run(["gh", "release", "view", tag, "--json", "assets", "-q", ".assets[].name"], capture=True)
    assets = r.stdout.strip().split('\n')
    print(f"  Assets: {', '.join(assets)}")

    r = run(["curl", "-sI", "https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1"], capture=True)
    status = r.stdout.split('\n')[0] if r.stdout else "unknown"
    print(f"  Install URL: {status.strip()}")

    # Step 8: Update local CLI (handle locked exe)
    print(f"\n=== Updating local CLI ===")
    local_dir = os.path.expanduser("~/.clibridge4unity")
    if os.path.isdir(local_dir):
        local_exe = os.path.join(local_dir, "clibridge4unity.exe")
        try:
            shutil.copy2(exe_path, local_exe)
            print(f"  Updated {local_exe}")
        except PermissionError:
            # Exe is locked (running) — try rename trick
            old_exe = local_exe + ".old"
            try:
                if os.path.exists(old_exe):
                    os.remove(old_exe)
                os.rename(local_exe, old_exe)
                shutil.copy2(exe_path, local_exe)
                try: os.remove(old_exe)
                except: pass
                print(f"  Updated {local_exe} (via rename)")
            except Exception as e:
                print(f"  WARNING: Could not update local CLI ({e})")
                print(f"  Run: cp {exe_path} {local_exe}")

    # Summary
    print(f"\n{'='*40}")
    print(f"  Version: {version}")
    print(f"  Release: https://github.com/oddgames/clibridge4unity/releases/tag/{tag}")
    print(f"  Install: irm https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1 | iex")
    print(f"{'='*40}")

if __name__ == "__main__":
    main()
