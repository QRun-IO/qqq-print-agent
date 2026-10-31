# Workflow compatibility — 2026-10-30

The `zizmor/self-repository` advisory recommends GitHub's newer `$/` syntax. This repository currently uses `./.github/workflows/...` **only for job-level reusable workflow calls**. GitHub documents that both forms select the same commit as the calling workflow. These are not step-level local actions that resolve from a potentially different workspace checkout.

The latest released actionlint, **1.7.12**, rejects `$/` reusable workflow calls. This was reproduced locally with the pinned version; the unchanged `./` calls pass. The upstream compatibility issue is still open. Retaining the supported job-level form preserves full actionlint validation of the called workflow, including its inputs and permissions, without suppressing the syntax rule or disabling any security check.

The advisory is reviewed and deferred for this specific use. No global zizmor exception is added, no branch requirement is removed, and no administrator bypass is used. External actions and workflows remain pinned to reviewed full commit hashes.

Adopt `$/` when a reviewed actionlint release supports it, update the checksum-pinned tool, and verify all PR and release workflows. Reassess separately if a step-level local or composite action is introduced; this decision does not cover that case.

Sources: [GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_iduses), [actionlint issue 711](https://github.com/rhysd/actionlint/issues/711), [current actionlint release](https://github.com/rhysd/actionlint/releases/tag/v1.7.12).

