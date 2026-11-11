---
pageId: developers/contributing
contentRevision: 7
---

# Contributing and wiki maintenance

Applies to: Rimlink 0.10.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Read repository instructions, status/diff and the relevant specification. Preserve other people's changes. Here Astra writes specifications/plans and one final review; the main Sol implements code and fixes. For substantial changes: approved design → plan → tests/implementation → one final review → verification.

Wiki maintenance is part of task completion. When changing features, behavioral fixes, config/CLI/API, installation, operations or recovery, update complete RU/EN articles, navigation and the reference inventory in the same task. Record sourcePaths and increment contentRevision in both versions and the manifest; give a concrete rationale if documentation is unaffected. A source snapshot does not authorize silently stale translations.

Run available checks and report pages/results/limitations. Keep secrets and internal handoff notes out of the published corpus. Commits verify the actual Git identity; push, PR and deployment require separate permission.

## Documentation is part of task completion {#contribution-dod}

Before implementation locate the feature spec and sourcePaths-linked articles. New behavior needs player/admin explanation; API/config changes need reference and source snapshot updates after manual reconciliation. Never refresh a digest merely to make CI green.

Each substantive topic needs a RU/EN question, anchor and exact source. Check parity and drift from the original base, then build/links/browser. A docs-impact exception binds to current base, changed-file hashes and reviewer; an old rationale cannot authorize future omission.

Report pages, executed commands and limitations. Editing wiki source does not authorize deployment: Git identity, staged/secrets checks and user instruction remain separate.

## Limits and safety {#limits}

Dated plans and verification reports are historical material, not current functionality promises. Keep release versions aligned in Directory.Build.props, ClientInfo.Version and About.xml; change protocol only for incompatibility. Pages deployment is manual, not an ordinary push side effect.

## See also {#related}

- [Testing and acceptance](./testing.md)
- [Localization](./localization.md)
- [Updating server and clients](../admins/updating.md)
