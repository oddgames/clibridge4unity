"""Code commands: CODE_SEARCH, CODE_ANALYZE, CODE_EXEC, CODE_EXEC_RETURN, TEST."""


class TestCodeSearch:
    def test_class_query(self, bridge):
        bridge.ok("CODE_SEARCH", "class:MonoBehaviour")

    def test_method_query(self, bridge):
        bridge.ok("CODE_SEARCH", "method:Start")

    def test_field_query(self, bridge):
        bridge.ok("CODE_SEARCH", "field:name")

    def test_inherits_query(self, bridge):
        bridge.ok("CODE_SEARCH", "inherits:Component")

    def test_attribute_query(self, bridge):
        bridge.ok("CODE_SEARCH", "attribute:Serializable")

    def test_empty_query_returns_results(self, bridge):
        # Empty query does a broad text search, returns results
        bridge.ok("CODE_SEARCH", "")


class TestCodeAnalyze:
    def test_class_name(self, bridge):
        bridge.ok("CODE_ANALYZE", "BridgeServer")

    def test_member(self, bridge):
        bridge.ok("CODE_ANALYZE", "CommandRegistry.Initialize")

    def test_nonexistent_returns_error(self, bridge):
        bridge.err("CODE_ANALYZE", "ZzzNonExistentClass12345")


class TestCodeExec:
    def test_simple_statement(self, bridge):
        bridge.ok("CODE_EXEC", 'Debug.Log("test from pytest");')

    def test_expression(self, bridge):
        bridge.ok("CODE_EXEC", "var x = 1 + 1;")


class TestCodeExecReturn:
    def test_math(self, bridge):
        out = bridge.ok("CODE_EXEC_RETURN", "return (1 + 1).ToString();")
        assert "2" in out

    def test_string(self, bridge):
        out = bridge.ok("CODE_EXEC_RETURN", 'return "hello";')
        assert "hello" in out

    def test_compile_error(self, bridge):
        bridge.err("CODE_EXEC_RETURN", "invalid{{{code")


class TestTestRunner:
    def test_list(self, bridge):
        bridge.ok("TEST")
