import importlib.util
import io
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("schema", Path(__file__).with_name("check-website-schema-ready.py"))
schema = importlib.util.module_from_spec(spec)
spec.loader.exec_module(schema)


class SchemaReadinessTests(unittest.TestCase):
    def test_two_consecutive_checks_are_required(self):
        for checks, result in (([True, False, True], False), ([False, True, True], True)):
            with self.subTest(checks=checks):
                readings = iter(checks)
                self.assertEqual(result, schema.stable_schema_ready(lambda: next(readings), lambda _: None, 3))

    def test_exact_build_and_schema_are_both_required(self):
        sha = "a" * 40
        for version, http_status, result in (
            (f"1.0.0+{sha}.{sha}", 200, True),
            (f"1.0.0+{sha}", 503, False),
            ("1.0.0+" + "b" * 40, 200, False),
        ):
            with self.subTest(version=version, http_status=http_status):
                status = io.BytesIO(('{"buildVersion":"' + version + '"}').encode())
                ready = io.BytesIO(b"")
                ready.status = http_status
                with patch.object(schema.urllib.request, "urlopen", side_effect=[status, ready]):
                    self.assertEqual(result, schema.verify_package("example", sha))

    def test_unknown_or_enabled_migration_settings_cannot_skip(self):
        for value in ("", "true", "unexpected"):
            with self.subTest(value=value), patch.object(schema.subprocess, "run") as run:
                run.return_value.stdout = value
                self.assertFalse(schema.safety_settings_are_off("group", "app"))

    def test_existing_diagnostics_configuration_is_required(self):
        for count, result in (("0", False), ("1", True)):
            with self.subTest(count=count), patch.object(schema.subprocess, "run") as run:
                run.side_effect = [SimpleNamespace(stdout=value) for value in ("false", "false", count)]
                self.assertEqual(result, schema.safety_settings_are_off("group", "app"))


if __name__ == "__main__":
    unittest.main()
