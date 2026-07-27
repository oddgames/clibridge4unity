"""Play mode commands: PLAY, STOP, PAUSE, STEP, PLAYMODE."""
import time


class TestPlayMode:
    def test_play(self, bridge):
        bridge.ok("PLAY")
        # Play-mode entry includes a domain reload — poll until Unity actually
        # reports Playing before issuing PAUSE/STEP, or they hit a half-loaded editor.
        for _ in range(15):
            code, out, _err = bridge.run("PLAYMODE")
            if code == 0 and "isplaying: true" in out.lower():
                break
            time.sleep(2)
        # Game window may pop up as a "dialog" — dismiss it so subsequent commands work
        bridge.run("DISMISS")

    def test_playmode_while_playing(self, bridge):
        out = bridge.ok("PLAYMODE")
        assert "isplaying: true" in out.lower()

    def test_pause(self, bridge):
        bridge.ok("PAUSE")

    def test_step(self, bridge):
        bridge.ok("STEP")

    def test_stop(self, bridge):
        bridge.ok("STOP")
        time.sleep(1)

    def test_playmode_after_stop(self, bridge):
        # Exiting play mode triggers a domain reload; wait until the bridge is
        # stable again so later test files don't inherit a half-reloaded editor.
        for _ in range(15):
            code, out, _err = bridge.run("STATUS")
            if code == 0 and "isCompiling: False" in out and "isPlaying: False" in out:
                break
            time.sleep(2)
        bridge.ok("PLAYMODE")
