"""Scene commands: CREATE, FIND, DELETE, SAVE, LOAD, SCENEVIEW, WINDOWS, GAMEVIEW."""
import re


class TestScene:
    def test_returns_info(self, bridge):
        out = bridge.ok("INSPECTOR", "scene")
        assert "Scene:" in out


class TestCreateFindDelete:
    def test_create(self, bridge):
        bridge.ok("CREATE", "SceneTestObj")

    def test_find_created(self, bridge):
        bridge.ok("FIND", "SceneTestObj")

    def test_delete(self, bridge):
        bridge.ok("DELETE", "SceneTestObj")

    def test_find_deleted_fails(self, bridge):
        bridge.err("FIND", "SceneTestObj")


class TestFindByComponentType:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "FindByCompObj")
        bridge.ok("COMPONENT_ADD", "FindByCompObj BoxCollider")

    def test_find_by_component_type(self, bridge):
        out = bridge.ok("FIND", "BoxCollider")
        assert "FindByCompObj" in out
        assert "matchedBy = BoxCollider" in out

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "FindByCompObj")


class TestInactiveObjects:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "InactiveTestObj")
        bridge.ok("CODE_EXEC_RETURN",
                  'GameObject.Find("InactiveTestObj").SetActive(false); return "off";')

    def test_find_shows_inactive(self, bridge):
        out = bridge.ok("FIND", "InactiveTestObj")
        assert "active = False" in out

    def test_inspector_resolves_inactive(self, bridge):
        out = bridge.ok("INSPECTOR", "InactiveTestObj")
        assert "active: False" in out

    def test_delete_resolves_inactive(self, bridge):
        bridge.ok("DELETE", "InactiveTestObj")


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
        code, out, err = bridge.run("SAVE")
        if "no path on disk" in out:
            # Untitled scene — save to an explicit path, then clean up
            bridge.ok("SAVE", "Assets/BridgeTestSave.unity")
            bridge.ok("ASSET_DELETE", "Assets/BridgeTestSave.unity")
        else:
            assert code == 0 and not out.startswith("Error:"), f"SAVE failed: {out}"


class TestLoad:
    def test_load_current_scene(self, bridge):
        out = bridge.ok("STATUS")
        match = re.search(r'currentScenePath:\s*(\S+\.unity)', out)
        if match:
            out = bridge.ok("LOAD", match.group(1), timeout=60)
            assert "now the active scene" in out
