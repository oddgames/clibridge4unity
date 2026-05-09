"""Core commands: PING, PROBE, DIAG, STATUS, HELP, LOG."""


class TestPing:
    def test_returns_pong(self, bridge):
        out = bridge.ok("PING")
        assert "Pong" in out


class TestProbe:
    def test_main_thread_ok(self, bridge):
        out = bridge.ok("PROBE")
        assert "OK" in out


class TestDiag:
    def test_returns_thread_info(self, bridge):
        out = bridge.ok("DIAG")
        assert "thread:" in out

    def test_contains_hwnd(self, bridge):
        out = bridge.ok("DIAG")
        assert "hwnd:" in out

    def test_contains_sync_context(self, bridge):
        out = bridge.ok("DIAG")
        assert "syncCtx:" in out

    def test_contains_process_info(self, bridge):
        out = bridge.ok("DIAG")
        assert "pid:" in out


class TestStatus:
    def test_returns_status(self, bridge):
        out = bridge.ok("STATUS")
        assert "isCompiling" in out

    def test_has_unity_version(self, bridge):
        out = bridge.ok("STATUS")
        assert "unityVersion" in out

    def test_has_project_path(self, bridge):
        out = bridge.ok("STATUS")
        assert "projectPath" in out

    def test_has_play_state(self, bridge):
        out = bridge.ok("STATUS")
        assert "isPlaying" in out


class TestHelp:
    def test_lists_commands(self, bridge):
        out = bridge.ok("HELP")
        assert "Unity Bridge Commands" in out

    def test_lists_ping(self, bridge):
        out = bridge.ok("HELP")
        assert "PING" in out

    def test_verbose(self, bridge):
        out = bridge.ok("HELP", "verbose")
        # Verbose output is longer and includes usage details indented below commands
        assert "PING" in out
        assert len(out) > 500

    def test_specific_command(self, bridge):
        out = bridge.ok("HELP", "PING")
        assert "PING" in out
        assert "Test connection" in out

    def test_unknown_command(self, bridge):
        out = bridge.ok("HELP", "NONEXISTENT")
        assert "Unknown command" in out


class TestLog:
    def test_default(self, bridge):
        bridge.ok("LOG")

    def test_last_5(self, bridge):
        bridge.ok("LOG", "last:5")

    def test_errors_filter(self, bridge):
        bridge.ok("LOG", "errors")


class TestCompile:
    def test_listed_in_help(self, bridge):
        out = bridge.ok("HELP", "COMPILE")
        assert "Force script recompilation" in out


class TestRefresh:
    def test_listed_in_help(self, bridge):
        out = bridge.ok("HELP", "REFRESH")
        assert "Force asset database refresh" in out


class TestMenu:
    def test_execute_console(self, bridge):
        out = bridge.ok("MENU", "Window/General/Console")
        assert "Executed" in out

    def test_invalid_menu(self, bridge):
        out = bridge.err("MENU", "NonExistent/Fake/Path")
        assert "not found" in out or "failed" in out

    def test_blocked_quit(self, bridge):
        out = bridge.err("MENU", "File/Quit")
        assert "Blocked" in out

    def test_no_args(self, bridge):
        out = bridge.err("MENU")
        assert "Usage" in out or "Error" in out


class TestProfile:
    def test_status(self, bridge):
        out = bridge.ok("PROFILE")
        assert "enabled" in out

    def test_enable_disable(self, bridge):
        out = bridge.ok("PROFILE", "enable")
        assert "enabled" in out
        out = bridge.ok("PROFILE", "disable")
        assert "disabled" in out

    def test_listed_in_help(self, bridge):
        out = bridge.ok("HELP", "PROFILE")
        assert "Profiler" in out or "profiler" in out
