"""Play mode commands: PLAY, STOP, PAUSE, STEP, PLAYMODE."""
import time


class TestPlayMode:
    def test_play(self, bridge):
        bridge.ok("PLAY")
        time.sleep(2)
        # Game window may pop up as a "dialog" — dismiss it so subsequent commands work
        bridge.run("DISMISS")

    def test_playmode_while_playing(self, bridge):
        out = bridge.ok("PLAYMODE")
        assert "Playing" in out or "playing" in out.lower()

    def test_pause(self, bridge):
        bridge.ok("PAUSE")

    def test_step(self, bridge):
        bridge.ok("STEP")

    def test_stop(self, bridge):
        bridge.ok("STOP")
        time.sleep(1)

    def test_playmode_after_stop(self, bridge):
        bridge.ok("PLAYMODE")
