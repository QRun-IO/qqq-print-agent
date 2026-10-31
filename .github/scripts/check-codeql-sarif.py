"""Reject missing/malformed CodeQL evidence before enforcing the severity gate."""
import json
import math
import os
from pathlib import Path


def check(directory, language, threshold):
    if not math.isfinite(threshold) or not 0 <= threshold <= 10:
        raise ValueError("Invalid security severity threshold")
    canonical = {"java-kotlin": "java", "javascript-typescript": "javascript", "c-cpp": "cpp"}.get(language, language)
    files = sorted(Path(directory).glob("*.sarif"))
    if not canonical or not any(path.name == canonical + ".sarif" for path in files):
        raise ValueError("Missing expected CodeQL SARIF report for " + language)
    blocking = []
    for path in files:
        report = json.loads(path.read_text(encoding="utf-8"))
        runs = report.get("runs")
        if report.get("version") != "2.1.0" or not isinstance(runs, list) or not runs:
            raise ValueError("Invalid or empty SARIF runs: " + path.name)
        for run in runs:
            driver = run.get("tool", {}).get("driver", {})
            results = run.get("results")
            if driver.get("name") != "CodeQL" or not isinstance(driver.get("rules"), list) or not isinstance(results, list):
                raise ValueError("Missing CodeQL tool/rules/results evidence: " + path.name)
            rules = {}
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
                if not math.isfinite(severity) or not 0 <= severity <= 10:
                    raise ValueError("Invalid rule severity: " + rule_id)
                if severity >= threshold:
                    blocking.append((severity, rule_id))
    for severity, rule_id in sorted(blocking, reverse=True):
        print(f"::error title=CodeQL security finding::{rule_id} ({severity})")
    print(f"Validated {len(files)} SARIF report(s); {len(blocking)} blocking result(s)")
    return bool(blocking)


if __name__ == "__main__":
    raise SystemExit(1 if check(os.environ["SARIF_DIR"], os.environ["EXPECTED_LANGUAGE"], float(os.environ["THRESHOLD"])) else 0)
