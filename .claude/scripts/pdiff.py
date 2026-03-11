#!/usr/bin/env python
"""Text diff for Plastic SCM — no GUI, just unified diff output.

Usage:
    pdiff.py                    # diff all changed text files
    pdiff.py path/to/file.cs   # diff specific file
    pdiff.py --stat             # summary only (like git diff --stat)
"""
import sys, os, subprocess, difflib, argparse, tempfile, shutil

BINARY_EXTS = {'.exe', '.dll', '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tga', '.psd',
               '.meta', '.asset', '.unity', '.prefab', '.mat', '.anim', '.controller',
               '.ttf', '.otf', '.woff', '.wav', '.mp3', '.ogg', '.fbx', '.obj',
               '.db', '.so', '.dylib', '.zip', '.gz', '.tar', '.7z'}

SKIP_PATHS = {'.git', 'Library', 'Temp', 'Logs', 'obj', 'bin'}

def run(cmd, timeout=30):
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=timeout)
    return r.stdout if r.returncode == 0 else None

def get_workspace_root():
    out = run("cm getworkspacefrompath .")
    return out.strip() if out else None

def get_changed_files():
    """Parse cm status --machinereadable for CH/CO files."""
    out = run("cm status --machinereadable")
    if not out:
        return []
    files = []
    for line in out.strip().split('\n'):
        parts = line.split(' ')
        if len(parts) >= 2 and parts[0] in ('CH', 'CO'):
            path = parts[1]
            if should_diff(path):
                files.append(path)
    return files

def should_diff(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in BINARY_EXTS:
        return False
    for p in path.replace('\\', '/').split('/'):
        if p in SKIP_PATHS:
            return False
    return os.path.isfile(path)

def batch_get_base(files, tmpdir):
    """Download all base revisions in a single cm cat call. Returns {filepath: base_content}."""
    if not files:
        return {}
    # Build revspec;output pairs
    pairs = []
    path_map = {}  # tmp_path -> original_path
    for i, fp in enumerate(files):
        tmp_out = os.path.join(tmpdir, f"base_{i}{os.path.splitext(fp)[1]}")
        pairs.append(f'"{fp};{tmp_out}"')
        path_map[tmp_out] = fp

    cmd = "cm cat " + " ".join(pairs)
    subprocess.run(cmd, shell=True, capture_output=True, text=True,
                   encoding="utf-8", errors="replace", timeout=60)

    result = {}
    for tmp_out, orig in path_map.items():
        if os.path.isfile(tmp_out):
            try:
                with open(tmp_out, 'r', encoding='utf-8', errors='replace') as f:
                    result[orig] = f.read()
            except (IOError, OSError):
                result[orig] = None
        else:
            result[orig] = None
    return result

def make_short(filepath, root):
    if root and filepath.startswith(root):
        return filepath[len(root):].lstrip('\\/')
    return os.path.basename(filepath)

def main():
    parser = argparse.ArgumentParser(description="Text diff for Plastic SCM")
    parser.add_argument('files', nargs='*', help='Specific files to diff')
    parser.add_argument('--stat', action='store_true', help='Show summary only')
    args = parser.parse_args()

    if args.files:
        files = [os.path.abspath(f) for f in args.files]
    else:
        files = get_changed_files()

    if not files:
        print("No changed text files found.")
        return

    root = get_workspace_root()

    # Batch-download all base revisions in one cm call
    tmpdir = tempfile.mkdtemp(prefix="pdiff_")
    try:
        bases = batch_get_base(files, tmpdir)
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    total_add = 0
    total_del = 0
    file_count = 0

    for filepath in files:
        base = bases.get(filepath)
        try:
            with open(filepath, 'r', encoding='utf-8', errors='replace') as f:
                working = f.read()
        except (IOError, OSError):
            continue

        if base is None and working is None:
            continue
        if base == working:
            continue

        short = make_short(filepath, root)
        diff = list(difflib.unified_diff(
            (base or '').splitlines(keepends=True),
            working.splitlines(keepends=True),
            fromfile=f"a/{short}", tofile=f"b/{short}"))
        if not diff:
            continue

        file_count += 1
        additions = sum(1 for l in diff if l.startswith('+') and not l.startswith('+++'))
        deletions = sum(1 for l in diff if l.startswith('-') and not l.startswith('---'))
        total_add += additions
        total_del += deletions

        if args.stat:
            total = additions + deletions
            width = min(total, 40)
            ratio = additions / max(total, 1)
            add_bar = '+' * max(1, int(ratio * width)) if additions else ''
            del_bar = '-' * max(1, int((1 - ratio) * width)) if deletions else ''
            print(f" {short:<50s} | {total:>4d} \033[32m{add_bar}\033[31m{del_bar}\033[0m")
        else:
            for line in diff:
                line = line.rstrip('\n')
                if line.startswith('+++') or line.startswith('---'):
                    print(f"\033[1m{line}\033[0m")
                elif line.startswith('+'):
                    print(f"\033[32m{line}\033[0m")
                elif line.startswith('-'):
                    print(f"\033[31m{line}\033[0m")
                elif line.startswith('@@'):
                    print(f"\033[36m{line}\033[0m")
                else:
                    print(line)
            print()

    if file_count == 0:
        print("No text changes detected.")
    else:
        summary = f" {file_count} file(s) changed, \033[32m+{total_add}\033[0m, \033[31m-{total_del}\033[0m"
        if args.stat:
            print(f"\n{summary}")
        else:
            print(summary)

if __name__ == "__main__":
    main()
