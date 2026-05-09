"""UI commands: UI_DISCOVER, SCREENSHOT."""
import os, re


class TestUIDiscover:
    def test_default(self, bridge):
        bridge.ok("UI_DISCOVER")

    def test_sprites(self, bridge):
        bridge.ok("UI_DISCOVER", "sprites")

    def test_fonts(self, bridge):
        bridge.ok("UI_DISCOVER", "fonts")

    def test_prefabs(self, bridge):
        bridge.ok("UI_DISCOVER", "prefabs")

    def test_scenes(self, bridge):
        bridge.ok("UI_DISCOVER", "scenes")


class TestUIRender:
    def test_setup_renderable_prefab(self, bridge):
        # Create a cube (has MeshRenderer) and save as prefab for render test
        bridge.ok("CREATE", '{"name":"RenderTestCube","components":["MeshFilter","MeshRenderer"]}')
        bridge.ok("PREFAB_SAVE", "RenderTestCube Assets/Prefabs")
        bridge.ok("DELETE", "RenderTestCube")

    def test_render_prefab(self, bridge):
        out = bridge.ok("SCREENSHOT", "Assets/Prefabs/RenderTestCube.prefab")
        # Extract the output PNG path and verify it's a real, non-empty image
        match = re.search(r'([A-Za-z]:\\[^\s\n]+\.png|/[^\s\n]+\.png)', out)
        if match:
            png_path = match.group(1)
            assert os.path.isfile(png_path), f"Rendered PNG not found: {png_path}"
            size = os.path.getsize(png_path)
            assert size > 100, f"Rendered PNG is too small ({size} bytes) - likely blank"
