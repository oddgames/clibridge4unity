"""Component commands: INSPECTOR, COMPONENT_ADD, COMPONENT_SET, COMPONENT_REMOVE."""


class TestInspector:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "CompTestObj")

    def test_shows_transform(self, bridge):
        out = bridge.ok("INSPECTOR", "CompTestObj")
        assert "Transform" in out

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "CompTestObj")


class TestComponentAdd:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "CompAddTest")

    def test_add_rigidbody(self, bridge):
        bridge.ok("COMPONENT_ADD", "CompAddTest Rigidbody")

    def test_inspector_has_rigidbody(self, bridge):
        out = bridge.ok("INSPECTOR", "CompAddTest")
        assert "Rigidbody" in out

    def test_add_box_collider(self, bridge):
        bridge.ok("COMPONENT_ADD", "CompAddTest BoxCollider")

    def test_add_light(self, bridge):
        bridge.ok("COMPONENT_ADD", "CompAddTest Light")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "CompAddTest")


class TestComponentSet:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "CompSetTest")
        bridge.ok("COMPONENT_ADD", "CompSetTest Rigidbody")

    def test_set_mass(self, bridge):
        bridge.ok("COMPONENT_SET", "CompSetTest Rigidbody mass 5")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "CompSetTest")


class TestComponentRemove:
    def test_setup(self, bridge):
        bridge.ok("CREATE", "CompRemoveTest")
        bridge.ok("COMPONENT_ADD", "CompRemoveTest Rigidbody")

    def test_remove_rigidbody(self, bridge):
        bridge.ok("COMPONENT_REMOVE", "CompRemoveTest Rigidbody")

    def test_inspector_no_rigidbody(self, bridge):
        out = bridge.ok("INSPECTOR", "CompRemoveTest")
        assert "Rigidbody" not in out

    def test_remove_nonexistent_fails(self, bridge):
        bridge.err("COMPONENT_REMOVE", "CompRemoveTest Rigidbody")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "CompRemoveTest")
