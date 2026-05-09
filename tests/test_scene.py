"""Scene commands: SCENE, CREATE, FIND, DELETE, SAVE, LOAD, SCENEVIEW, WINDOWS, GAMEVIEW."""
import re


class TestScene:
    def test_returns_info(self, bridge):
        out = bridge.ok("SCENE")
        assert "sceneName" in out


class TestCreateFindDelete:
    def test_create(self, bridge):
        bridge.ok("CREATE", "SceneTestObj")

    def test_find_created(self, bridge):
        bridge.ok("FIND", "SceneTestObj")

    def test_delete(self, bridge):
        bridge.ok("DELETE", "SceneTestObj")

    def test_find_deleted_fails(self, bridge):
        bridge.err("FIND", "SceneTestObj")


class TestCreateWithJson:
    def test_create_with_position(self, bridge):
        bridge.ok("CREATE", '{"name":"SceneTestPos","position":{"x":1,"y":2,"z":3}}')

    def test_find_positioned(self, bridge):
        bridge.ok("FIND", "SceneTestPos")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "SceneTestPos")


class TestSceneView:
    def test_frame(self, bridge):
        bridge.ok("SCENEVIEW", "frame")

    def test_2d(self, bridge):
        bridge.ok("SCENEVIEW", "2d")

    def test_3d(self, bridge):
        bridge.ok("SCENEVIEW", "3d")


class TestWindows:
    def test_lists_windows(self, bridge):
        out = bridge.ok("WINDOWS")
        assert "Scene" in out


class TestGameView:
    def test_1280x720(self, bridge):
        bridge.ok("GAMEVIEW", "1280x720")

    def test_1920x1080(self, bridge):
        bridge.ok("GAMEVIEW", "1920x1080")


class TestSave:
    def test_save(self, bridge):
        bridge.ok("SAVE")


class TestLoad:
    def test_load_current_scene(self, bridge):
        out = bridge.ok("SCENE")
        match = re.search(r'"?path"?\s*[:=]\s*"?([^\s"]+\.unity)', out)
        if match:
            bridge.ok("LOAD", match.group(1))
