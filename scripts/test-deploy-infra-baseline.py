import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("baseline", Path(__file__).with_name("deploy-infra-baseline.py"))
baseline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(baseline)


class ReleasePathTests(unittest.TestCase):
    def test_only_identical_proof_can_enable_reuse(self):
        previous = {"outputs":{"webAppName":"example"}, "source":"commit-tree", "observed":"live-hash"}
        with tempfile.TemporaryDirectory() as directory:
            proof = Path(directory)/"proof.json"
            proof.write_text(json.dumps(previous))
            output = Path(directory)/"out"
            for current in (previous, dict(previous, source="changed"), dict(previous, observed="drift"), None):
                with self.subTest(current=current), patch.dict(os.environ, GITHUB_OUTPUT=str(output)), \
                     patch.object(baseline.sys, "argv", ["script", "reuse", str(proof)]), \
                     patch.object(baseline, "observe", return_value=current,
                                  side_effect=TimeoutError if current is None else None):
                    output.write_text("")
                    if current == previous:
                        baseline.main()
                        self.assertIn("reuse=true", output.read_text())
                    else:
                        with self.assertRaises((ValueError, TimeoutError)):
                            baseline.main()
                        self.assertEqual("", output.read_text())


if __name__ == "__main__":
    unittest.main()
