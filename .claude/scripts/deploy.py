#!/usr/bin/env python
"""Deploy clibridge4unity: build, package, push, tag, release, upload, verify."""
import sys, os, subprocess, shutil, tempfile, time

def run(cmd, check=True, capture=False, timeout=120):
    """Run a shell command, print it, return result."""
    print(f"  $ {cmd}")
    r = subprocess.run(cmd, shell=True, capture_output=capture, text=True, timeout=timeout)
    if check and r.returncode != 0:
        if capture:
            print(r.stdout)
            print(r.stderr, file=sys.stderr)
        print(f"FAILED: exit {r.returncode}", file=sys.stderr)
        sys.exit(1)
    return r

def main():
    if len(sys.argv) < 2:
        print("Usage: deploy.py <version>", file=sys.stderr)
        sys.exit(1)

    version = sys.argv[1]
    tag = f"v{version}"
    root = subprocess.run("git rev-parse --show-toplevel", shell=True, capture_output=True, text=True).stdout.strip()
    if not root:
        print("ERROR: not in a git repo", file=sys.stderr)
        sys.exit(1)
    os.chdir(root)

    exe_path = os.path.join(root, "clibridge4unity/bin/Release/net8.0/win-x64/publish/clibridge4unity.exe")
    tools_path = os.path.join(root, "Package/Tools/win-x64/clibridge4unity.exe")

    # Step 1: Build
    print(f"\n=== Building v{version} ===")
    run("cd clibridge4unity && dotnet clean -c Release -v q && dotnet publish -c Release", timeout=180)
    if not os.path.isfile(exe_path):
        print(f"ERROR: exe not found at {exe_path}", file=sys.stderr)
        sys.exit(1)
    size_mb = os.path.getsize(exe_path) / 1024 / 1024
    print(f"  Built: {exe_path} ({size_mb:.1f} MB)")

    # Step 2: Verify version
    print(f"\n=== Verifying version ===")
    r = run(f"{exe_path} --version", capture=True)
    if version not in r.stdout:
        print(f"ERROR: exe reports '{r.stdout.strip()}', expected {version}", file=sys.stderr)
        sys.exit(1)
    print(f"  OK: {r.stdout.strip()}")

    # Step 3: Package
    print(f"\n=== Packaging ===")
    shutil.copy2(exe_path, tools_path)
    print(f"  Copied exe to {tools_path}")

    zip_path = os.path.join(tempfile.gettempdir(), "clibridge4unity-win-x64.zip")
    run(f'powershell -NoProfile -Command "Compress-Archive -Path \'{exe_path}\' -DestinationPath \'{zip_path}\' -Force"')
    print(f"  Created: {zip_path}")

    # Step 4: Git commit and push
    print(f"\n=== Git commit and push ===")
    run("git add -A")
    run(f'git commit -m "Release {tag}"')
    run("git push origin main", timeout=180)

    # Step 5: Tag and release
    print(f"\n=== Creating tag and release ===")
    run(f"git tag {tag}", check=False)
    run(f"git push origin {tag}", timeout=180, check=False)
    run(f'gh release create {tag} --title "{tag}" --generate-notes', check=False)

    # Step 6: Upload assets
    print(f"\n=== Uploading assets ===")
    run(f'gh release upload {tag} "{zip_path}" --clobber', timeout=180)
    run(f'gh release upload {tag} "{exe_path}" --clobber', timeout=180)

    # Step 7: Verify
    print(f"\n=== Verifying release ===")
    r = run(f'gh release view {tag} --json assets -q ".assets[].name"', capture=True)
    assets = r.stdout.strip().split('\n')
    print(f"  Assets: {', '.join(assets)}")

    r = run('curl -sI "https://raw.githubusercontent.com/oddgames/clibridge4unity/main/install.ps1"', capture=True)
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
