"""Error handling: unknown commands, nonexistent objects, bad arguments."""


class TestUnknownCommand:
    def test_returns_error(self, bridge):
        bridge.err("NONEXISTENT_CMD")


class TestFindErrors:
    def test_nonexistent_object(self, bridge):
        bridge.err("FIND", "ZzzDoesNotExist99999")


class TestDeleteErrors:
    def test_nonexistent_object(self, bridge):
        bridge.err("DELETE", "ZzzDoesNotExist99999")


class TestInspectorErrors:
    def test_nonexistent_object(self, bridge):
        bridge.err("INSPECTOR", "ZzzDoesNotExist99999")


class TestComponentErrors:
    def test_add_bad_object(self, bridge):
        bridge.err("COMPONENT_ADD", "ZzzDoesNotExist99999 Rigidbody")

    def test_remove_bad_object(self, bridge):
        bridge.err("COMPONENT_REMOVE", "ZzzDoesNotExist99999 Rigidbody")

    def test_set_bad_object(self, bridge):
        bridge.err("COMPONENT_SET", "ZzzDoesNotExist99999 Rigidbody mass 5")


class TestLoadErrors:
    def test_nonexistent_scene(self, bridge):
        bridge.err("LOAD", "Assets/Scenes/NonExistent.unity")
