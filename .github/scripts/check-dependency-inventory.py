# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Require scanner evidence for every resolved NuGet lock-file dependency."""

import json
import sys
from pathlib import Path
from typing import TypedDict


class LockedPackage(TypedDict, total=False):
   """Describe the resolved NuGet fields consumed by inventory comparison."""

   type: str
   resolved: str


class Lock(TypedDict, total=False):
   """Map framework names to their resolved dependency entries."""

   dependencies: dict[str, dict[str, LockedPackage]]


class Package(TypedDict, total=False):
   """Describe the Trivy and CycloneDX coordinate spellings."""

   Name: str
   Version: str
   name: str
   version: str
   purl: str


class Result(TypedDict, total=False):
   """Hold packages analyzed in one Trivy result."""

   Packages: list[Package]


class Report(TypedDict, total=False):
   """Describe the supported scanner inventories without changing validation."""

   Results: list[Result]
   bomFormat: str
   components: list[Package]


MAX_REPORTED_MISSING = 10
OSV_STATUS_ARGUMENT_COUNT = 3


def expected_packages(root: str | Path) -> set[tuple[str, str]]:
   """Collect every selected package/version from owned NuGet lock files."""
   packages: set[tuple[str, str]] = set()
   for path in Path(root).rglob("packages.lock.json"):
      if any(part in {"bin", "obj", ".git", ".trivycache"} for part in path.parts):
         continue
      lock: Lock = json.loads(path.read_text(encoding="utf-8-sig"))
      for framework in lock.get("dependencies", {}).values():
         for name, package in framework.items():
            if package.get("type", "").lower() == "project":
               continue
            version = package.get("resolved")
            if not isinstance(version, str) or not version:
               raise ValueError("Missing resolved NuGet version in " + str(path))
            packages.add((name.lower(), version))
   required = {"microsoft.data.sqlite.core", "microsoft.windowsappsdk", "koftwentytwo.appkit.winui"}
   if not required.issubset({name for name, _ in packages}):
      raise ValueError("Expected application NuGet lock graph is missing")
   return packages


def check(mode: str, report_path: str | Path, root: str | Path) -> None:
   """Require the report to cover the entire selected NuGet graph."""
   expected = expected_packages(root)
   report: Report = json.loads(Path(report_path).read_text(encoding="utf-8"))
   if mode == "trivy":
      packages = [package for result in report.get("Results", []) for package in result.get("Packages", [])]
      actual = {(package.get("Name", "").lower(), package.get("Version")) for package in packages}
   elif mode == "sbom":
      if report.get("bomFormat") != "CycloneDX":
         raise ValueError("Expected a CycloneDX SBOM")
      packages = report.get("components", [])
      actual = {(package.get("name", "").lower(), package.get("version")) for package in packages if package.get("purl", "").lower().startswith("pkg:nuget/")}
   else:
      raise ValueError("Unknown inventory mode")
   missing = expected - actual
   if missing:
      raise ValueError(f"Scanner omitted {len(missing)} resolved NuGet package/version(s): {sorted(missing)[:MAX_REPORTED_MISSING]}")
   print(f"Validated {mode} inventory: all {len(expected)} resolved NuGet package/version(s) present")


def check_osv_status(status: int) -> None:
   """Accept only successful OSV scans, including scans reporting findings."""
   if status not in (0, 1):
      raise ValueError(f"OSV failed or reported no packages (exit {status})")


if __name__ == "__main__":
   if len(sys.argv) == OSV_STATUS_ARGUMENT_COUNT and sys.argv[1] == "osv-status":
      check_osv_status(int(sys.argv[2]))
   else:
      check(*sys.argv[1:])
