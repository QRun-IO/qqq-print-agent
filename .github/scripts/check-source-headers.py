# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Check leading notices in explicitly owned, Git-tracked source families."""

import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


def selected(name: str, policy: dict[str, Any]) -> bool:
   """Select owned paths, honoring documented exact/pattern exclusions first."""
   if any(fnmatch.fnmatchcase(name, item["pattern"]) for item in policy["exclusions"]):
      return False
   return any(fnmatch.fnmatchcase(name, pattern) for pattern in policy["includes"])


def leading_comments(text: str, style: str) -> str:
   """Read comments only, preserving required interpreter/XML directive ordering."""
   text = text.removeprefix("\ufeff")
   if text.startswith("<?xml"):
      declaration = re.match(r"<\?xml[^?]*\?>", text)
      if style != "xml" or declaration is None:
         raise ValueError("Invalid XML declaration")
      text = text[declaration.end():]
   elif text.startswith("#!"):
      if style not in ("hash", "powershell", "slash"):
         raise ValueError("Unexpected interpreter directive")
      text = text.partition("\n")[2]
   while re.match(r"#Requires\b", text, re.IGNORECASE):
      if style != "powershell":
         raise ValueError("Unexpected PowerShell directive")
      text = text.partition("\n")[2]
   comments = []
   while text.strip():
      text = text.lstrip()
      if text.startswith("<?xml") or re.match(r"#!|#Requires\b", text, re.IGNORECASE):
         raise ValueError("Required declaration must precede the notice")
      if style in ("hash", "powershell") and text.startswith("#"):
         line, _, text = text.partition("\n")
         comments.append(line[1:])
      elif style == "slash" and text.startswith("//"):
         line, _, text = text.partition("\n")
         comments.append(line[2:])
      elif ((style == "slash" and text.startswith("/*"))
            or (style == "xml" and text.startswith("<!--"))
            or (style == "powershell" and text.startswith("<#"))):
         opening, closing = {"slash": ("/*", "*/"), "xml": ("<!--", "-->"), "powershell": ("<#", "#>")}[style]
         body, separator, rest = text[len(opening):].partition(closing)
         if not separator:
            raise ValueError("Unterminated leading comment")
         comments.append(body)
         text = rest
      else:
         break
   return "\n".join(re.sub(r"^\s*[*~]\s?", "", line).strip() for line in "\n".join(comments).splitlines())


def check_notice(name: str, text: str, policy: dict[str, Any]) -> None:
   """Require an approved holder/license pair in real leading comment syntax."""
   suffix = Path(name).suffix
   style = policy["styles"].get(suffix)
   if Path(name).name.startswith("Dockerfile."):
      style = "hash"
   if style is None:
      raise ValueError("Selected path has no configured comment syntax")
   comments = leading_comments(text, style)
   approved = [item for item in policy["notices"] if any(fnmatch.fnmatchcase(name, pattern) for pattern in item["paths"])]
   for notice in approved:
      holder = re.escape(notice["holder"])
      copyright_pattern = rf"(?m)^Copyright\s+(?:\([cC]\)\s*)?\d{{4}}(?:[-–]\d{{4}})?\.?\s+{holder}(?:\s+\(KofTwentyTwo\))?\.?(?:\s+Licensed under the MIT License \(see LICENSE\)\.)?$"
      if re.search(copyright_pattern, comments) is None:
         continue
      spdx = re.findall(r"(?m)^SPDX-License-Identifier:\s*(\S+)\s*$", comments)
      if spdx and any(value != notice["license"] for value in spdx):
         continue
      apache = all(phrase in comments for phrase in (
         "Licensed under the Apache License, Version 2.0",
         "you may not use this file except in compliance with the License.",
         "https://www.apache.org/licenses/LICENSE-2.0",
         "WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND",
         "limitations under the License.",
      ))
      if suffix == ".java" and not apache:
         continue
      if notice["license"] in spdx or (notice["license"] == "Apache-2.0" and apache):
         return
   raise ValueError("Missing or mismatched leading copyright/license notice")


def check_repository(root: Path, policy: dict[str, Any]) -> int:
   """Check tracked sources without scanning build outputs or executing source."""
   tracked = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True, capture_output=True).stdout.decode("utf-8").split("\u0000")
   failures = []
   count = 0
   for name in tracked:
      if not name or not selected(name, policy):
         continue
      count += 1
      try:
         check_notice(name, (root / name).read_text(encoding="utf-8-sig"), policy)
      except (ValueError, OSError) as error:
         failures.append(f"{name}: {error}")
   if count == 0:
      raise ValueError("No owned source files selected")
   if failures:
      raise ValueError("\n".join(failures))
   return count


def main() -> None:
   """Load the repository-specific reviewed scope and fail CI on a bad notice."""
   root = Path(__file__).resolve().parents[2]
   policy = json.loads((root / ".github/source-headers.json").read_text(encoding="utf-8"))
   count = check_repository(root, policy)
   print(f"Leading source notices verified: {count} tracked files")


if __name__ == "__main__":
   try:
      main()
   except (ValueError, OSError, subprocess.CalledProcessError) as error:
      print(str(error), file=sys.stderr)
      sys.exit(1)
