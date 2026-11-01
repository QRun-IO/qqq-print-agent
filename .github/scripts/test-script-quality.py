# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Prove the quality gates reject real violations, using private temporary fixtures and the locked tools."""

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import ModuleType
from typing import Any, override

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[2]
GOOD_PYTHON = '"""Probe module."""\n\n\ndef add(left: int, right: int) -> int:\n   """Add two integers."""\n   return left + right\n'
UNUSED_IMPORT_PYTHON = '"""Probe module."""\n\nimport os\n\n\ndef add(left: int, right: int) -> int:\n   """Add two integers."""\n   return left + right\n'
TWO_SPACE_PYTHON = '"""Probe module."""\n\n\ndef add(left: int, right: int) -> int:\n  """Add two integers."""\n  return left + right\n'
INCOMPATIBLE_PYTHON = '"""Probe module."""\n\nVALUE: int = "text"\n'
UNANNOTATED_PYTHON = '"""Probe module."""\n\n\ndef echo(value):\n   """Return the value."""\n   return value\n'
GOOD_JAVASCRIPT = "export function addNumbers(left, right)\n{\n   return left + right;\n}\n"
BAD_LAYOUT_JAVASCRIPT = "export function addNumbers(left, right) {\n  return left + right\n}\n"
GOOD_TYPED_JAVASCRIPT = "/** @type {number} */\nexport const value = 1;\n"
INCOMPATIBLE_JAVASCRIPT = '/** @type {number} */\nexport const value = "text";\n'


def load(name: str) -> ModuleType:
   """Load the real sibling guard for tests."""
   spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
   assert spec is not None and spec.loader is not None
   module = importlib.util.module_from_spec(spec)
   spec.loader.exec_module(module)
   return module


guard = load("check-script-quality")


def run(command: list[str], cwd: Path, stdin: str | None = None) -> subprocess.CompletedProcess[str]:
   """Run one real tool invocation without a shell and capture its diagnostics."""
   return subprocess.run(command, cwd=cwd, input=stdin, check=False, capture_output=True, text=True, encoding="utf-8")  # noqa: S603 -- Fixed tool paths and private temporary arguments; no shell.


def ruff(arguments: list[str], source: Path) -> subprocess.CompletedProcess[str]:
   """Run the locked Ruff with the repository configuration on one private fixture."""
   return run([sys.executable, "-m", "ruff", *arguments, "--no-cache", "--config", str(ROOT / "ruff.toml"), str(source)], source.parent)


class ScopeTests(unittest.TestCase):
   """Reject tracked sources that fall outside the explicit tool roots."""

   def test_python_outside_script_root_is_rejected(self) -> None:
      """Reject a tracked Python file outside the script root instead of letting it disappear."""
      for stray in ("tools/helper.py", ".github/scripts/nested/helper.py", "helper.pyi"):
         with self.subTest(stray=stray), self.assertRaisesRegex(ValueError, "outside the checked script root"):
            guard.python_scope([".github/scripts/check-a.py", stray])

   def test_python_scope_returns_sorted_script_root_and_rejects_empty(self) -> None:
      """Select only the direct script root, sorted, and reject selecting nothing."""
      self.assertEqual([".github/scripts/a.py", ".github/scripts/b.py"], guard.python_scope([".github/scripts/b.py", "README.md", ".github/scripts/a.py"]))
      with self.assertRaisesRegex(ValueError, "No owned Python"):
         guard.python_scope(["README.md"])

   def test_javascript_requires_tsconfig_include_scope(self) -> None:
      """Reject tracked JavaScript outside tsconfig include patterns, including .js/.cjs/.ts."""
      with tempfile.TemporaryDirectory() as name:
         root = Path(name)
         (root / "tsconfig.json").write_text(json.dumps({"include": ["tools/**/*.mjs", "eslint.config.mjs"]}), encoding="utf-8")
         self.assertEqual(["eslint.config.mjs", "tools/a/b.mjs", "tools/c.mjs"], guard.javascript_scope(root, ["tools/c.mjs", "tools/a/b.mjs", "eslint.config.mjs", "README.md"]))
         for stray in ("src/z.js", "tools/w.cjs", "tools/v.ts", "tools/u.js"):
            with self.subTest(stray=stray), self.assertRaisesRegex(ValueError, "outside the tsconfig include list"):
               guard.javascript_scope(root, ["tools/c.mjs", stray])
         with self.assertRaisesRegex(ValueError, "No owned JavaScript"):
            guard.javascript_scope(root, ["README.md"])

   def test_javascript_without_tsconfig_is_rejected(self) -> None:
      """Reject JavaScript in a repository that has no tsconfig."""
      with tempfile.TemporaryDirectory() as name:
         root = Path(name)
         self.assertEqual([], guard.javascript_scope(root, ["README.md"]))
         with self.assertRaisesRegex(ValueError, "without a tsconfig.json"):
            guard.javascript_scope(root, ["tools/a.mjs"])


class PyrightCountTests(unittest.TestCase):
   """Reject Pyright reports that analyzed too few files or found anything."""

   @staticmethod
   def report(analyzed: int, errors: int = 0, warnings: int = 0) -> dict[str, Any]:
      """Build the exact Pyright JSON shape consumed by the guard."""
      return {"summary": {"filesAnalyzed": analyzed, "errorCount": errors, "warningCount": warnings}, "generalDiagnostics": [{"file": "x.py", "message": "finding"}] * (errors + warnings), "exitCode": 1 if errors or warnings else 0}

   def test_nonzero_exit_fails_even_with_clean_summary(self) -> None:
      """Fail a nonzero Pyright exit even when every count is clean."""
      clean = self.report(4)
      clean["exitCode"] = 1
      with self.assertRaisesRegex(ValueError, "exited 1"):
         guard.check_pyright_report(clean, 4)

   def test_count_must_match_selected_sources(self) -> None:
      """Fail zero or mismatched analyzed counts even with no diagnostics."""
      guard.check_pyright_report(self.report(4), 4)
      for analyzed in (0, 3, 5):
         with self.subTest(analyzed=analyzed), self.assertRaisesRegex(ValueError, "analyzed"):
            guard.check_pyright_report(self.report(analyzed), 4)

   def test_errors_and_warnings_fail(self) -> None:
      """Fail on errors and on warnings at the exact count."""
      for report in (self.report(4, errors=1), self.report(4, warnings=1)):
         with self.subTest(report=report), self.assertRaisesRegex(ValueError, "reported findings"):
            guard.check_pyright_report(report, 4)


class RuffProbeTests(unittest.TestCase):
   """Run the real locked Ruff on bounded private fixtures."""

   def test_compliant_fixture_passes_and_violations_fail(self) -> None:
      """Pass a compliant control and reject unused imports and wrong indentation."""
      with tempfile.TemporaryDirectory() as name:
         probe = Path(name) / "probe.py"
         probe.write_text(GOOD_PYTHON, encoding="utf-8", newline="\n")
         self.assertEqual(0, ruff(["check"], probe).returncode)
         self.assertEqual(0, ruff(["format", "--check"], probe).returncode)
         probe.write_text(UNUSED_IMPORT_PYTHON, encoding="utf-8", newline="\n")
         unused = ruff(["check", "--output-format", "concise"], probe)
         self.assertNotEqual(0, unused.returncode)
         self.assertIn("F401", unused.stdout)
         probe.write_text(TWO_SPACE_PYTHON, encoding="utf-8", newline="\n")
         indented = ruff(["format", "--check"], probe)
         self.assertNotEqual(0, indented.returncode)
         self.assertIn("would be reformatted", (indented.stdout + indented.stderr).lower())


class PyrightProbeTests(unittest.TestCase):
   """Run the real locked Pyright with the repository strict settings on private projects."""

   @staticmethod
   def project(root: Path, include: list[str] | None = None) -> None:
      """Write the repository Pyright config, without its venv location, into a private project."""
      config: dict[str, Any] = json.loads((ROOT / ".github/pyrightconfig.json").read_text(encoding="utf-8"))
      del config["venvPath"], config["venv"]
      if include is not None:
         config["include"] = include
      (root / "pyrightconfig.json").write_text(json.dumps(config), encoding="utf-8")

   def test_compliant_control_passes_and_strict_violations_fail(self) -> None:
      """Analyze the control once with no findings and reject incompatible and unannotated code."""
      with tempfile.TemporaryDirectory() as name:
         root = Path(name)
         self.project(root)
         scripts = root / "scripts"
         scripts.mkdir()
         probe = scripts / "probe.py"
         probe.write_text(GOOD_PYTHON, encoding="utf-8", newline="\n")
         clean = guard.run_pyright(root, ".")
         self.assertEqual(0, clean["exitCode"])
         guard.check_pyright_report(clean, 1)
         clean["exitCode"] = 1
         with self.assertRaisesRegex(ValueError, "exited 1"):
            guard.check_pyright_report(clean, 1)
         probe.write_text(INCOMPATIBLE_PYTHON, encoding="utf-8", newline="\n")
         report = guard.run_pyright(root, ".")
         self.assertIn("reportAssignmentType", {item.get("rule") for item in report["generalDiagnostics"]})
         with self.assertRaisesRegex(ValueError, "reported findings"):
            guard.check_pyright_report(report, 1)
         probe.write_text(UNANNOTATED_PYTHON, encoding="utf-8", newline="\n")
         strict = guard.run_pyright(root, ".")
         self.assertIn("reportMissingParameterType", {item.get("rule") for item in strict["generalDiagnostics"]})

   def test_zero_analyzed_files_cannot_pass(self) -> None:
      """Reject an empty include and a hidden-directory root config in the wrapper."""
      with tempfile.TemporaryDirectory() as name:
         root = Path(name)
         scripts = root / ".github" / "scripts"
         scripts.mkdir(parents=True)
         (scripts / "probe.py").write_text(GOOD_PYTHON, encoding="utf-8", newline="\n")
         for include in (["nothing/*.py"], [".github/scripts/*.py"]):
            self.project(root, include)
            with self.subTest(include=include), self.assertRaises(ValueError):
               guard.check_pyright_report(guard.run_pyright(root, "."), 1)


class JavaScriptProbeTests(unittest.TestCase):
   """Run the real locked ESLint and TypeScript on bounded private input."""

   @override
   def setUp(self) -> None:
      """Skip only where tsconfig is absent, because the scope guard forbids JavaScript there."""
      if not (ROOT / "tsconfig.json").is_file():
         self.skipTest("No JavaScript tooling in this repository")
      node = shutil.which("node")
      self.assertIsNotNone(node)
      self.node = str(node)

   def eslint(self, text: str) -> subprocess.CompletedProcess[str]:
      """Lint text as an in-scope tool file using the repository configuration."""
      return run([self.node, str(ROOT / "node_modules/eslint/bin/eslint.js"), "--stdin", "--stdin-filename", "tools/probe.mjs", "--max-warnings", "0", "--format", "json"], ROOT, text)

   def test_eslint_rejects_wrong_layout(self) -> None:
      """Pass a compliant control and reject K&R braces, two-space indent and a missing semicolon."""
      good = self.eslint(GOOD_JAVASCRIPT)
      self.assertEqual(0, good.returncode, good.stdout + good.stderr)
      bad = self.eslint(BAD_LAYOUT_JAVASCRIPT)
      self.assertNotEqual(0, bad.returncode)
      rules = {message["ruleId"] for message in json.loads(bad.stdout)[0]["messages"]}
      self.assertLessEqual({"@stylistic/brace-style", "@stylistic/semi", "@stylistic/indent"}, rules)

   def test_checked_javascript_rejects_incompatible_assignment(self) -> None:
      """Reject a real incompatible assignment under the repository strict checkJs settings."""
      with tempfile.TemporaryDirectory() as name:
         root = Path(name)
         (root / "tsconfig.json").write_text(json.dumps({"extends": (ROOT / "tsconfig.json").as_posix(), "compilerOptions": {"typeRoots": [(ROOT / "node_modules/@types").as_posix()]}, "include": ["probe.mjs"]}), encoding="utf-8")
         tsc = [self.node, str(ROOT / "node_modules/typescript/bin/tsc"), "-p", str(root / "tsconfig.json")]
         (root / "probe.mjs").write_text(GOOD_TYPED_JAVASCRIPT, encoding="utf-8", newline="\n")
         good = run(tsc, root)
         self.assertEqual(0, good.returncode, good.stdout + good.stderr)
         (root / "probe.mjs").write_text(INCOMPATIBLE_JAVASCRIPT, encoding="utf-8", newline="\n")
         bad = run(tsc, root)
         self.assertNotEqual(0, bad.returncode)
         self.assertIn("TS2322", bad.stdout)


if __name__ == "__main__":
   unittest.main()
