# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Exercise the exact CI guards using synthetic evidence only."""

import contextlib
import copy
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import ModuleType
from typing import Any, cast, override

sys.dont_write_bytecode = True


def load(name: str) -> ModuleType:
   """Load the real sibling guard for synthetic evidence tests."""
   spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
   assert spec is not None and spec.loader is not None
   module = importlib.util.module_from_spec(spec)
   spec.loader.exec_module(module)
   return module


codeql = load("check-codeql-sarif")


class SarifTests(unittest.TestCase):
   """Reject incomplete SARIF evidence and preserve severity enforcement."""

   @override
   def setUp(self) -> None:
      """Create isolated synthetic evidence with cleanup after each test."""
      self.temporary = tempfile.TemporaryDirectory()
      self.addCleanup(self.temporary.cleanup)
      self.directory = Path(self.temporary.name)
      self.report: dict[str, Any] = {"version": "2.1.0", "runs": [{"tool": {"driver": {"name": "CodeQL", "rules": [{"id": "synthetic", "properties": {"security-severity": "8.0"}}]}}, "results": []}]}

   def save(self, report: object) -> None:
      """Write the test body as SARIF without validating it first."""
      (self.directory / "java.sarif").write_text(json.dumps(report), encoding="utf-8")

   def test_clean_report_with_zero_findings_passes(self) -> None:
      """Verify clean report with zero findings passes."""
      self.save(self.report)
      self.assertFalse(codeql.check(self.directory, "java-kotlin", 7))

   def test_missing_report_or_wrong_language_fails(self) -> None:
      """Verify missing report or wrong language fails."""
      with self.assertRaises(ValueError):
         codeql.check(self.directory, "java-kotlin", 7)
      self.save(self.report)
      with self.assertRaises(ValueError):
         codeql.check(self.directory, "csharp", 7)

   def test_empty_or_malformed_report_fails(self) -> None:
      """Verify empty or malformed report fails."""
      for text in ["", "{", "null", "[]"]:
         with self.subTest(text=text):
            (self.directory / "java.sarif").write_text(text)
            with self.assertRaises((ValueError, AttributeError)):
               codeql.check(self.directory, "java-kotlin", 7)

   def test_missing_runs_tool_rules_or_results_fails(self) -> None:
      """Verify missing runs tool rules or results fails."""
      variants: list[dict[str, Any]] = [{"version": "2.1.0"}, {"version": "2.1.0", "runs": []}]
      for field in ["tool", "results"]:
         bad = copy.deepcopy(self.report)
         del bad["runs"][0][field]
         variants.append(bad)
      bad = copy.deepcopy(self.report)
      bad["runs"][0]["tool"]["driver"]["rules"] = []
      variants.append(bad)
      for bad in variants:
         with self.subTest(report=bad):
            self.save(bad)
            with self.assertRaises(ValueError):
               codeql.check(self.directory, "java-kotlin", 7)

   def test_high_finding_blocks_and_unknown_rule_fails(self) -> None:
      """Verify high finding blocks and unknown rule fails."""
      self.report["runs"][0]["results"] = [{"ruleId": "synthetic"}]
      self.save(self.report)
      with contextlib.redirect_stdout(io.StringIO()):
         self.assertTrue(codeql.check(self.directory, "java-kotlin", 7))
      self.report["runs"][0]["results"] = [{"ruleId": "missing"}]
      self.save(self.report)
      with self.assertRaises(ValueError):
         codeql.check(self.directory, "java-kotlin", 7)

   def test_failed_invocation_and_invalid_threshold_fail(self) -> None:
      """Verify failed invocation and invalid threshold fail."""
      self.report["runs"][0]["invocations"] = [{"executionSuccessful": False}]
      self.save(self.report)
      with self.assertRaises(ValueError):
         codeql.check(self.directory, "java-kotlin", 7)
      with self.assertRaises(ValueError):
         codeql.check(self.directory, "java-kotlin", float("nan"))


inventory = load("check-dependency-inventory")


class InventoryTests(unittest.TestCase):
   """Exercise selected dependency coverage using isolated synthetic reports."""

   @override
   def setUp(self) -> None:
      """Create isolated synthetic evidence with cleanup after each test."""
      self.temporary = tempfile.TemporaryDirectory()
      self.addCleanup(self.temporary.cleanup)
      self.root = Path(self.temporary.name)
      self.names = ["Microsoft.Data.Sqlite.Core", "Microsoft.WindowsAppSDK", "KofTwentyTwo.AppKit.WinUI", "Synthetic.Transitive"]
      lock = {"dependencies": {"net10": {name: {"type": "Transitive", "resolved": "1.0.0"} for name in self.names}}}
      (self.root / "packages.lock.json").write_text(json.dumps(lock))
      self.report = self.root / "report.json"

   def test_complete_trivy_inventory_passes_and_missing_package_fails(self) -> None:
      """Verify complete trivy inventory passes and missing package fails."""
      packages = [{"Name": name, "Version": "1.0.0"} for name in self.names]
      self.report.write_text(json.dumps({"Results": [{"Packages": packages}]}))
      inventory.check("trivy", self.report, self.root)
      packages.pop()
      self.report.write_text(json.dumps({"Results": [{"Packages": packages}]}))
      with self.assertRaises(ValueError):
         inventory.check("trivy", self.report, self.root)

   def test_nonempty_sbom_must_contain_expected_nuget_graph(self) -> None:
      """Verify nonempty sbom must contain expected nuget graph."""
      packages = [{"name": name, "version": "1.0.0", "purl": "pkg:nuget/" + name + "@1.0.0"} for name in self.names]
      self.report.write_text(json.dumps({"bomFormat": "CycloneDX", "components": packages}))
      inventory.check("sbom", self.report, self.root)
      for components in [[], packages[:1], [{"name": "unrelated", "version": "1", "purl": "pkg:npm/unrelated@1"}]]:
         with self.subTest(components=components):
            self.report.write_text(json.dumps({"bomFormat": "CycloneDX", "components": components}))
            with self.assertRaises(ValueError):
               inventory.check("sbom", self.report, self.root)

   def test_empty_or_wrong_version_inventory_fails(self) -> None:
      """Verify empty or wrong version inventory fails."""
      for report in cast(list[object], [{}, {"Results": []}, {"Results": [{"Packages": [{"Name": name, "Version": "0.0.0"} for name in self.names]}]}]):
         with self.subTest(report=report):
            self.report.write_text(json.dumps(report))
            with self.assertRaises(ValueError):
               inventory.check("trivy", self.report, self.root)

   def test_osv_empty_inventory_or_engine_failure_is_rejected(self) -> None:
      """Verify osv empty inventory or engine failure is rejected."""
      for status in [0, 1]:
         inventory.check_osv_status(status)
      for status in [2, 127, 128]:
         with self.subTest(status=status), self.assertRaises(ValueError):
            inventory.check_osv_status(status)

   def test_missing_lock_graph_fails(self) -> None:
      """Verify missing lock graph fails."""
      (self.root / "packages.lock.json").unlink()
      with self.assertRaises(ValueError):
         inventory.expected_packages(self.root)


if __name__ == "__main__":
   unittest.main()
