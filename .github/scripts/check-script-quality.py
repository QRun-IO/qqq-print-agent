# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Fail closed unless every tracked owned script is covered by, and really analyzed by, the quality tools."""

import hashlib
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path, PurePosixPath
from typing import Any

PYTHON_SUFFIXES = (".py", ".pyi", ".pyw")
SCRIPT_PATTERN = ".github/scripts/*.py"
PYRIGHT_CONFIG = ".github"
JAVASCRIPT_SUFFIXES = (".js", ".cjs", ".mjs", ".jsx", ".ts", ".mts", ".cts", ".tsx")
# EX-0015: the only npm package whose license classification the PR gate may exempt (the matcher ignores versions, so this guard pins it).
URI_JS_PURL = "pkg:npm/uri-js"
URI_JS_VERSION = "4.4.1"
URI_JS_INTEGRITY = "sha512-7rKUyy33Q1yc98pQ1DAmLtwX109F7TIfWlW1Ydo8Wl1ii1SeHieeh0HHfPeL2fMXK6z0s8ecKs9frCuLJvndBg=="
URI_JS_LICENSE_SHA256 = "0af366eff4c01ec147c9c61ea9e8ffad64a4294754c9d79355f3fd1b97cb2fb9"
PR_GATES = ".github/workflows/pr-gates.yml"
# Microsoft's official npm Pyright, restored from package-lock.json by `npm ci --ignore-scripts`.
PYRIGHT_ENTRY = Path(__file__).resolve().parents[2] / "node_modules" / "pyright" / "index.js"


def tracked_files(root: Path) -> list[str]:
   """List tracked and not-yet-ignored new paths so no source file can silently bypass the gates."""
   # Fixed Git inventory operation; the root is an argument, never shell input.
   output = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"], check=True, capture_output=True).stdout.decode("utf-8")  # noqa: S603, S607 -- Resolve Git from the developer/CI PATH; no shell or user-supplied command.
   return [name for name in output.split("\u0000") if name]


def python_scope(tracked: list[str]) -> list[str]:
   """Return the tracked Python sources, rejecting any outside the one checked script root."""
   candidates = sorted(name for name in tracked if name.endswith(PYTHON_SUFFIXES))
   outside = [name for name in candidates if not PurePosixPath(name).full_match(SCRIPT_PATTERN)]
   if outside:
      raise ValueError("Tracked Python outside the checked script root: " + ", ".join(outside))
   if not candidates:
      raise ValueError("No owned Python scripts selected")
   return candidates


def javascript_scope(root: Path, tracked: list[str]) -> list[str]:
   """Return tracked JavaScript/TypeScript sources, rejecting any outside the tsconfig include list."""
   candidates = sorted(name for name in tracked if name.endswith(JAVASCRIPT_SUFFIXES))
   tsconfig = root / "tsconfig.json"
   if not tsconfig.is_file():
      if candidates:
         raise ValueError("Tracked JavaScript/TypeScript without a tsconfig.json: " + ", ".join(candidates))
      return []
   includes: list[str] = json.loads(tsconfig.read_text(encoding="utf-8"))["include"]
   outside = [name for name in candidates if not any(PurePosixPath(name).full_match(pattern) for pattern in includes)]
   if outside:
      raise ValueError("Tracked JavaScript/TypeScript outside the tsconfig include list: " + ", ".join(outside))
   if not candidates:
      raise ValueError("No owned JavaScript sources selected")
   return candidates


def run_pyright(root: Path, config: str) -> dict[str, Any]:
   """Run the locked npm Pyright through Node with JSON output and return its full report."""
   node = shutil.which("node")
   if node is None:
      raise ValueError("Node.js is required to run the locked Pyright")
   if not PYRIGHT_ENTRY.is_file():
      raise ValueError(f"Locked Pyright is missing; run npm ci --ignore-scripts ({PYRIGHT_ENTRY})")
   # Resolved Node and locked entry script; the config is an argument, never shell input.
   completed = subprocess.run([node, str(PYRIGHT_ENTRY), "-p", config, "--outputjson"], cwd=root, check=False, capture_output=True, text=True, encoding="utf-8")  # noqa: S603 -- Resolved Node and locked entry script; no shell.
   try:
      report: dict[str, Any] = json.loads(completed.stdout[completed.stdout.index("{") :])
      int(report["summary"]["filesAnalyzed"])  # Fail here when the summary shape is missing.
      report["exitCode"] = completed.returncode
   except (ValueError, KeyError) as error:
      raise ValueError(f"Pyright produced no JSON summary (exit {completed.returncode}): {completed.stdout}{completed.stderr}") from error
   return report


def check_pyright_report(report: dict[str, Any], expected: int) -> None:
   """Require the analyzed-file count to match the selected sources, with no errors or warnings."""
   summary = report["summary"]
   analyzed = summary["filesAnalyzed"]
   if analyzed == 0 or analyzed != expected:
      raise ValueError(f"Pyright analyzed {analyzed} files, expected {expected}")
   if summary["errorCount"] or summary["warningCount"]:
      details = [f"{item['file']}: {item['message']}" for item in report["generalDiagnostics"]]
      raise ValueError("Pyright reported findings:\n" + "\n".join(details))
   if report["exitCode"] != 0:
      raise ValueError(f"Pyright exited {report['exitCode']} despite a clean summary")


def run_ruff(root: Path, arguments: list[str], sources: list[str]) -> None:
   """Run Ruff on the explicit source list so no selected file can be skipped."""
   # Fixed interpreter module invocation; paths are arguments, never shell input.
   if subprocess.run([sys.executable, "-m", "ruff", *arguments, "--no-cache", *sources], cwd=root, check=False).returncode != 0:  # noqa: S603 -- Fixed interpreter and module; no shell.
      raise ValueError("Ruff " + " ".join(arguments) + " failed")


def check_uri_js_exception(root: Path) -> None:
   """Allow the EX-0015 license exception only for the reviewed, unmodified, development-only uri-js."""
   packages: dict[str, dict[str, Any]] = json.loads((root / "package-lock.json").read_text(encoding="utf-8"))["packages"]
   copies = [entry for path, entry in packages.items() if path.rsplit("node_modules/", 1)[-1] == "uri-js"]
   if len(copies) != 1:
      raise ValueError(f"EX-0015 covers exactly one locked uri-js copy, found {len(copies)}")
   if copies[0].get("version") != URI_JS_VERSION or copies[0].get("integrity") != URI_JS_INTEGRITY or copies[0].get("dev") is not True:
      raise ValueError(f"EX-0015 covers only development-only uri-js {URI_JS_VERSION} with the reviewed integrity")
   root_entry = packages.get("", {})
   if "uri-js" in {*root_entry.get("dependencies", {}), *root_entry.get("devDependencies", {})}:
      raise ValueError("EX-0015 covers uri-js only as a transitive tool dependency, not a direct one")
   consumers = [path for path, entry in packages.items() if "uri-js" in entry.get("dependencies", {}) and entry.get("dev") is not True]
   if consumers:
      raise ValueError("uri-js is required by a non-development package: " + ", ".join(consumers))
   digest = hashlib.sha256((root / "node_modules" / "uri-js" / "LICENSE").read_bytes()).hexdigest()
   if digest != URI_JS_LICENSE_SHA256:
      raise ValueError(f"Installed uri-js LICENSE bytes differ from the reviewed EX-0015 copy ({digest})")
   caller = [line for line in (root / PR_GATES).read_text(encoding="utf-8").splitlines() if not line.lstrip().startswith("#")]
   listed = re.findall(r"pkg:npm/[^\s,]+", "\n".join(caller))
   if listed != [URI_JS_PURL]:
      raise ValueError(f"EX-0015 permits only {URI_JS_PURL} in allow-dependencies-licenses, found {listed}")


def main() -> None:
   """Verify scope, then Ruff format/lint and strict Pyright with a positive analyzed count."""
   root = Path(__file__).resolve().parents[2]
   tracked = tracked_files(root)
   sources = python_scope(tracked)
   javascript = javascript_scope(root, tracked)
   run_ruff(root, ["format", "--check"], sources)
   run_ruff(root, ["check"], sources)
   report = run_pyright(root, PYRIGHT_CONFIG)
   check_pyright_report(report, len(sources))
   check_uri_js_exception(root)
   print(f"Script quality verified: {len(sources)} Python scripts analyzed by Ruff and Pyright, {len(javascript)} JavaScript files in tsconfig scope")


if __name__ == "__main__":
   try:
      main()
   except (ValueError, OSError, subprocess.CalledProcessError) as error:
      print(str(error), file=sys.stderr)
      sys.exit(1)
