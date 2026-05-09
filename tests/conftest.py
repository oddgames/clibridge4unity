"""Shared fixtures for clibridge4unity integration tests."""
import subprocess, os, pytest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
CLI = os.path.expanduser("~/.clibridge4unity/clibridge4unity.exe")
PROJECT = os.path.join(ROOT, "UnityTestProject")


class Bridge:
    """Helper to run CLI commands against Unity."""

    def __init__(self):
        self.cli = CLI
        self.project = PROJECT

    def run(self, command, data="", timeout=30):
        """Run a command, return (exit_code, stdout, stderr)."""
        args = [self.cli, "-d", self.project, command]
        if data:
            args.append(data)
        r = subprocess.run(args, capture_output=True, text=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        return r.returncode, r.stdout.strip(), r.stderr.strip()

    def ok(self, command, data="", timeout=30):
        """Run a command and assert success. Returns stdout."""
        code, out, err = self.run(command, data, timeout)
        assert code == 0, f"{command} exited {code}: {err or out}"
        assert not out.startswith("Error:"), f"{command} returned error: {out}"
        return out

    def err(self, command, data="", timeout=30):
        """Run a command and assert it fails. Returns stdout."""
        code, out, err = self.run(command, data, timeout)
        assert code != 0 or out.startswith("Error:"), \
            f"{command} expected failure but got: {out}"
        return out


@pytest.fixture(scope="session")
def bridge():
    """Session-scoped bridge fixture. Checks connection once."""
    assert os.path.isfile(CLI), f"CLI not found: {CLI}"
    assert os.path.isdir(os.path.join(PROJECT, "Assets")), f"Project not found: {PROJECT}"

    b = Bridge()
    code, out, err = b.run("PING")
    if code != 0 or out.startswith("Error:"):
        pytest.exit(f"Cannot connect to Unity: {err or out}", returncode=1)
    return b
