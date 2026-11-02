# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

"""Exercise notice parsing, provenance, scope and real Git inventory rejection."""

import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from typing import Any

SPEC = importlib.util.spec_from_file_location("headers", Path(__file__).with_name("check-source-headers.py"))
assert SPEC is not None and SPEC.loader is not None
HEADERS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HEADERS)
POLICY = json.loads(Path(__file__).parents[1].joinpath("source-headers.json").read_text(encoding="utf-8"))
MIT = "Copyright (c) 2026 James Maes\nSPDX-License-Identifier: MIT"
APACHE = """Copyright (C) 2021-2025. Kingsrook, LLC
Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at
https://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License."""


def fixture_policy(holder: str = "James Maes", license_id: str = "MIT") -> dict[str, Any]:
   """Use the production styles with a small explicit owned fixture scope."""
   return {"includes": ["src/*"], "exclusions": [], "styles": POLICY["styles"], "notices": [{"holder": holder, "license": license_id, "paths": ["src/*"]}]}


class HeaderTests(unittest.TestCase):
   """Ensure policy failures cannot pass through text-only copyright searches."""

   def test_each_real_comment_syntax(self) -> None:
      """Recognize hash, slash, block and XML notices in their own languages."""
      for name, text in (
         ("src/a.py", "# " + MIT.replace("\n", "\n# ") + "\nprint(1)"),
         ("src/a.cs", "// " + MIT.replace("\n", "\n// ") + "\nclass A {}"),
         ("src/a.mjs", "/*\n * " + MIT.replace("\n", "\n * ") + "\n */\nexport {};"),
         ("src/a.xml", "<!--\n" + MIT + "\n-->\n<root/>"),
         ("src/a.ps1", "<#\n" + MIT + "\n#>\nWrite-Output 1"),
      ):
         with self.subTest(name=name):
            HEADERS.check_notice(name, text, fixture_policy())

   def test_year_range_separator_is_hyphen_or_en_dash_only(self) -> None:
      """Accept a hyphen or en dash between years and reject any other separator."""
      for separator, accepted in (("-", True), (chr(0x2013), True), ("/", False), (chr(0x2014), False)):
         text = f"# Copyright 2025{separator}2026 James Maes\n# SPDX-License-Identifier: MIT"
         with self.subTest(separator=separator):
            if accepted:
               HEADERS.check_notice("src/a.py", text, fixture_policy())
            else:
               with self.assertRaises(ValueError):
                  HEADERS.check_notice("src/a.py", text, fixture_policy())

   def test_required_first_lines_and_bom(self) -> None:
      """Accept shebangs, PowerShell requirements and XML declarations first."""
      for name, prefix, body in (
         ("src/a.sh", "#!/bin/sh\n", "# " + MIT.replace("\n", "\n# ")),
         ("src/a.ps1", "#Requires -Version 7.4\n", "# " + MIT.replace("\n", "\n# ")),
         ("src/a.xml", '<?xml version="1.0"?>\n', "<!--" + MIT + "--><root/>"),
         ("src/a.mjs", "#!/usr/bin/env node\n", "/*" + MIT + "*/"),
      ):
         with self.subTest(name=name):
            HEADERS.check_notice(name, "\ufeff" + prefix + body, fixture_policy())

   def test_misordered_required_first_lines_fail(self) -> None:
      """A notice must not displace an interpreter or XML declaration."""
      for name, text in (
         ("src/a.sh", "# " + MIT.replace("\n", "\n# ") + "\n#!/bin/sh"),
         ("src/a.ps1", "# " + MIT.replace("\n", "\n# ") + "\n#Requires -Version 7.4"),
         ("src/a.xml", "<!--" + MIT + '--><?xml version="1.0"?><root/>'),
      ):
         with self.subTest(name=name), self.assertRaises(ValueError):
            HEADERS.check_notice(name, text, fixture_policy())

   def test_code_strings_and_late_comments_do_not_count(self) -> None:
      """Reject docstrings and comments after executable declarations."""
      for text in ('notice = "' + MIT.replace("\n", "\\n") + '"', '"""' + MIT + '"""', "import os\n# " + MIT.replace("\n", "\n# ")):
         with self.subTest(text=text), self.assertRaises(ValueError):
            HEADERS.check_notice("src/a.py", text, fixture_policy())

   def test_wrong_syntax_and_unterminated_comments_fail(self) -> None:
      """Notice text alone is insufficient without a complete language comment."""
      for text in ("# " + MIT, "/*" + MIT, 'string text = "' + MIT + '";'):
         with self.subTest(text=text), self.assertRaises(ValueError):
            HEADERS.check_notice("src/a.cs", text, fixture_policy())
      with self.assertRaises(ValueError):
         HEADERS.check_notice("src/a.py", "<#" + MIT + "#>", fixture_policy())

   def test_wrong_holder_or_license_and_conflicting_spdx_fail(self) -> None:
      """Do not conflate repository licenses or accept conflicting identifiers."""
      for text in (MIT.replace("James Maes", "Someone Else"), MIT.replace("MIT", "Apache-2.0"), MIT + "\nSPDX-License-Identifier: Apache-2.0", MIT.splitlines()[0]):
         with self.subTest(text=text), self.assertRaises(ValueError):
            HEADERS.check_notice("src/a.cs", "/*" + text + "*/", fixture_policy())

   def test_complete_legacy_apache_without_spdx(self) -> None:
      """Retain valid legacy year ranges and complete Apache notices."""
      policy = fixture_policy("Kingsrook, LLC", "Apache-2.0")
      HEADERS.check_notice("src/A.java", "/*" + APACHE + "*/\nclass A {}", policy)
      HEADERS.check_notice("src/a.xml", "<!--" + APACHE + "--><root/>", policy)

   def test_java_requires_full_apache_block(self) -> None:
      """An abbreviated SPDX-only Java notice cannot replace scoped convention."""
      policy = fixture_policy("Kingsrook, LLC", "Apache-2.0")
      for notice in ("Copyright 2026 Kingsrook, LLC\nSPDX-License-Identifier: Apache-2.0", APACHE.replace("limitations under the License.", "")):
         with self.subTest(notice=notice), self.assertRaises(ValueError):
            HEADERS.check_notice("src/A.java", "/*" + notice + "*/", policy)

   def test_virtual_agent_provenance_is_path_scoped(self) -> None:
      """Only the owned virtual-agent family may use its existing QRun.IO holder."""
      policy = fixture_policy()
      policy["notices"].append({"holder": "QRun.IO, Inc.", "license": "MIT", "paths": ["tools/virtual-agent/*"]})
      text = "/*Copyright 2026 QRun.IO, Inc. Licensed under the MIT License (see LICENSE).\nSPDX-License-Identifier: MIT*/"
      HEADERS.check_notice("tools/virtual-agent/a.mjs", text, policy)
      with self.assertRaises(ValueError):
         HEADERS.check_notice("src/a.mjs", text, policy)

   def test_current_scope_and_documented_exclusions(self) -> None:
      """New scripts enter the guard; data, generated and upstream files do not."""
      self.assertTrue(HEADERS.selected(".github/scripts/new-tool.py", POLICY))
      self.assertTrue(HEADERS.selected(".github/workflows/new-check.yml", POLICY))
      for name in (".mvn/wrapper/MavenWrapperDownloader.java", "mvnw", "schemas/new.json", "src/main/resources/openapi/new.yaml", "src/test/resources/openapi/old.yaml", "src/generated/Example.cs", "src/Example/obj/Generated.cs", "packages.lock.json"):
         with self.subTest(name=name):
            self.assertFalse(HEADERS.selected(name, POLICY))
      self.assertTrue(all(item["reason"].strip() for item in POLICY["exclusions"]))

   def test_guard_sources_match_this_repository_provenance(self) -> None:
      """Check copied guard files against local policy even before Git staging."""
      for filename in ("check-source-headers.py", "test-source-headers.py"):
         path = Path(__file__).with_name(filename)
         name = ".github/scripts/" + filename
         self.assertTrue(HEADERS.selected(name, POLICY))
         HEADERS.check_notice(name, path.read_text(encoding="utf-8"), POLICY)

   def test_real_git_inventory_rejects_missing_tracked_header(self) -> None:
      """A tracked new source fails while untracked generated output is ignored."""
      with tempfile.TemporaryDirectory() as directory:
         root = Path(directory)
         subprocess.run(["git", "init", "-q", str(root)], check=True)  # noqa: S603, S607 -- Fixed Git command in the owned temporary test directory.
         root.joinpath("src").mkdir()
         source = root / "src/a.py"
         source.write_text("print(1)", encoding="utf-8")
         subprocess.run(["git", "-C", str(root), "add", "--", "src/a.py"], check=True)  # noqa: S603, S607 -- Fixed synthetic path; never a shell command.
         with self.assertRaisesRegex(ValueError, "src/a.py"):
            HEADERS.check_repository(root, fixture_policy())
         source.write_text("# " + MIT.replace("\n", "\n# ") + "\nprint(1)", encoding="utf-8")
         root.joinpath("src/untracked.py").write_text("print(2)", encoding="utf-8")
         self.assertEqual(1, HEADERS.check_repository(root, fixture_policy()))


if __name__ == "__main__":
   unittest.main()
