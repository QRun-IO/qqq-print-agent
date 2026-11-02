# Owned source notice check

Run these commands from the repository root with the existing Python 3 interpreter:

```text
python3 .github/scripts/test-source-headers.py
python3 .github/scripts/check-source-headers.py
```

The standard-library checker enforces the scope of the reviewed 2026-11-01 header pass, not an estate-wide licensing or formatting audit. `.github/source-headers.json` names the owned source/config path families, comment syntax, accepted holder/license pairs and explicit exclusion reasons. Python `fnmatch` patterns match slash-containing relative Git paths; new files in those families are covered after they are tracked. Empty selections fail. The checker does not scan untracked build output or modify files.

Only leading language comments count. Copyright text in a string, Python docstring or comment after code is rejected. UTF-8 BOMs, initial shebangs, PowerShell `#Requires` directives and XML declarations are handled before the notice. These declarations cannot be displaced beneath a new notice. PowerShell block comments are recognized only for PowerShell files. Existing years and holders are not rewritten. Complete canonical Apache notices are valid without redundant SPDX; Java still requires the full Apache block. A contradictory SPDX identifier fails.

JSON/schema/fixture/lock data, explicit generated output directories, vendor legal assets and upstream Maven wrappers are excluded with reasons in the policy. Solution/activation/test-runner metadata and `.editorconfig` remain outside this completed pass; their applicable root LICENSE/NOTICE remains authoritative. No broad claim about third-party licensing follows from this check. New owned file types or provenance must be reviewed before changing the allowlist; do not add blanket exclusions to make a failure disappear.

The implementation and its negative tests are intentionally maintained copies across the three printing repositories, with each repository's own copyright/license header and independently reviewed scope. They do not fetch policy or execute source. CI invokes them in existing jobs using the runner's available Python, with no new package or workflow context. Existing native C# IDE0073 and Java Checkstyle conventions remain unchanged.
This client accepts James Maes / MIT in its reviewed source/build/config families and preserves QRun.IO, Inc. / MIT solely in tools/virtual-agent. The existing scripts / virtual-agent job runs the guard.
