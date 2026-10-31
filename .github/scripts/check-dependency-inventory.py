"""Require scanner evidence for every resolved NuGet lock-file dependency."""
import json
from pathlib import Path
import sys


def expected_packages(root):
    packages = set()
    for path in Path(root).rglob("packages.lock.json"):
        if any(part in {"bin", "obj", ".git", ".trivycache"} for part in path.parts):
            continue
        lock = json.loads(path.read_text(encoding="utf-8-sig"))
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


def check(mode, report_path, root):
    expected = expected_packages(root)
    report = json.loads(Path(report_path).read_text(encoding="utf-8"))
    if mode == "trivy":
        packages = [package for result in report.get("Results", []) for package in result.get("Packages", [])]
        actual = {(package.get("Name", "").lower(), package.get("Version")) for package in packages}
    elif mode == "sbom":
        if report.get("bomFormat") != "CycloneDX":
            raise ValueError("Expected a CycloneDX SBOM")
        packages = report.get("components", [])
        actual = {(package.get("name", "").lower(), package.get("version")) for package in packages
                  if package.get("purl", "").lower().startswith("pkg:nuget/")}
    else:
        raise ValueError("Unknown inventory mode")
    missing = expected - actual
    if missing:
        raise ValueError(f"Scanner omitted {len(missing)} resolved NuGet package/version(s): {sorted(missing)[:10]}")
    print(f"Validated {mode} inventory: all {len(expected)} resolved NuGet package/version(s) present")


def check_osv_status(status):
    if status not in (0, 1):
        raise ValueError(f"OSV failed or reported no packages (exit {status})")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "osv-status":
        check_osv_status(int(sys.argv[2]))
    else:
        check(*sys.argv[1:])
