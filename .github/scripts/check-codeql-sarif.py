# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Reject missing/malformed CodeQL evidence before enforcing the severity gate."""

import json
import math
import os
from pathlib import Path
from typing import TypedDict

RuleProperties = TypedDict("RuleProperties", {"security-severity": str | float | None}, total=False)


class Rule(TypedDict):
   """Keep the rule identity and optional severity metadata used by the gate."""

   id: str
   properties: RuleProperties


class Component(TypedDict, total=False):
   """Describe CodeQL driver or extension rule metadata."""

   name: str
   rules: list[Rule]


class Tool(TypedDict, total=False):
   """Group the driver and optional extension rule catalogs."""

   driver: Component
   extensions: list[Component]


class Result(TypedDict, total=False):
   """Describe only the result fields consumed by the severity gate."""

   ruleId: str
   rule: dict[str, str]
   suppressions: list[object]


class Run(TypedDict, total=False):
   """Retain invocation success and findings for one analyzed language."""

   tool: Tool
   results: list[Result]
   invocations: list[dict[str, bool]]


class Report(TypedDict, total=False):
   """Represent SARIF fields while retaining the explicit runtime validation."""

   version: str
   runs: list[Run]


MAX_SEVERITY = 10


def check(directory: str | Path, language: str, threshold: float) -> bool:  # noqa: PLR0912 -- Keep the ordered evidence checks together; none may be skipped.
   """Validate complete CodeQL evidence and report findings at the threshold."""
   if not math.isfinite(threshold) or not 0 <= threshold <= MAX_SEVERITY:
      raise ValueError("Invalid security severity threshold")
   canonical = {"java-kotlin": "java", "javascript-typescript": "javascript", "c-cpp": "cpp"}.get(language, language)
   files = sorted(Path(directory).glob("*.sarif"))
   if not canonical or not any(path.name == canonical + ".sarif" for path in files):
      raise ValueError("Missing expected CodeQL SARIF report for " + language)
   blocking: list[tuple[float, str]] = []
   for path in files:
      report: Report = json.loads(path.read_text(encoding="utf-8"))
      runs = report.get("runs")
      if report.get("version") != "2.1.0" or not isinstance(runs, list) or not runs:
         raise ValueError("Invalid or empty SARIF runs: " + path.name)
      for run in runs:
         driver = run.get("tool", {}).get("driver", {})
         results = run.get("results")
         if driver.get("name") != "CodeQL" or not isinstance(driver.get("rules"), list) or not isinstance(results, list):
            raise ValueError("Missing CodeQL tool/rules/results evidence: " + path.name)
         rules: dict[str, Rule] = {}
         for component in [driver, *run.get("tool", {}).get("extensions", [])]:
            for rule in component.get("rules", []):
               rules[rule["id"]] = rule
         if not rules:
            raise ValueError("CodeQL report has no analyzed rule metadata: " + path.name)
         for invocation in run.get("invocations", []):
            if invocation.get("executionSuccessful") is False:
               raise ValueError("CodeQL invocation failed: " + path.name)
         for result in results:
            rule_id = result.get("ruleId") or result.get("rule", {}).get("id")
            if rule_id not in rules:
               raise ValueError("Result references missing rule metadata: " + path.name)
            if result.get("suppressions"):
               continue
            severity = float(rules[rule_id].get("properties", {}).get("security-severity", 0) or 0)
            if not math.isfinite(severity) or not 0 <= severity <= MAX_SEVERITY:
               raise ValueError("Invalid rule severity: " + rule_id)
            if severity >= threshold:
               blocking.append((severity, rule_id))
   for severity, rule_id in sorted(blocking, reverse=True):
      print(f"::error title=CodeQL security finding::{rule_id} ({severity})")
   print(f"Validated {len(files)} SARIF report(s); {len(blocking)} blocking result(s)")
   return bool(blocking)


if __name__ == "__main__":
   raise SystemExit(1 if check(os.environ["SARIF_DIR"], os.environ["EXPECTED_LANGUAGE"], float(os.environ["THRESHOLD"])) else 0)
