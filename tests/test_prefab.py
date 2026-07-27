"""Prefab commands: PREFAB_CREATE, PREFAB_SAVE, PREFAB_INSTANTIATE (hierarchy via INSPECTOR)."""


class TestPrefabSave:
    def test_create_object(self, bridge):
        bridge.ok("CREATE", "PrefabSaveTest")

    def test_save_as_prefab(self, bridge):
        bridge.ok("PREFAB_SAVE", "PrefabSaveTest Assets/Prefabs")

    def test_hierarchy(self, bridge):
        out = bridge.ok("INSPECTOR", "Assets/Prefabs/PrefabSaveTest.prefab --children")
        assert "PrefabSaveTest" in out

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "PrefabSaveTest")


class TestPrefabInstantiate:
    def test_instantiate(self, bridge):
        bridge.ok("PREFAB_INSTANTIATE", "Assets/Prefabs/PrefabSaveTest.prefab")

    def test_find_instance(self, bridge):
        bridge.ok("FIND", "PrefabSaveTest")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "PrefabSaveTest")


class TestPrefabCreate:
    def test_create_object(self, bridge):
        bridge.ok("CREATE", "PrefabCreateTest")

    def test_create_prefab(self, bridge):
        bridge.ok("PREFAB_CREATE", "PrefabCreateTest Assets/Prefabs/PrefabCreateTest.prefab")

    def test_cleanup(self, bridge):
        bridge.ok("DELETE", "PrefabCreateTest")
