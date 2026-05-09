"""Asset commands: ASSET_SEARCH."""


class TestAssetSearch:
    def test_by_type_prefab(self, bridge):
        bridge.ok("ASSET_SEARCH", "t:Prefab")

    def test_by_type_script(self, bridge):
        bridge.ok("ASSET_SEARCH", "t:Script")

    def test_by_type_material(self, bridge):
        bridge.ok("ASSET_SEARCH", "t:Material")

    def test_by_name(self, bridge):
        bridge.ok("ASSET_SEARCH", "PrefabSaveTest")

    def test_no_query_fails(self, bridge):
        bridge.err("ASSET_SEARCH", "")


class TestAssetReserialize:
    def test_specific_asset(self, bridge):
        out = bridge.ok("ASSET_RESERIALIZE", "Assets/Prefabs/RenderTestCube.prefab")
        assert "Reserialized" in out

    def test_nonexistent_fails(self, bridge):
        bridge.err("ASSET_RESERIALIZE", "Assets/DoesNotExist.prefab")
