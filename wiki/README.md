# Rimlink Wiki — maintenance

47 complete RU/EN source article pairs, VitePress 1.6.4, Node 24 (see .node-version), project base /rimlink/. This worktree has not been published. The existing site is https://asofwar.github.io/rimlink/ (RU/EN), using legacy GitHub Pages from gh-pages:/.

The Wiki baseline was imported without a Git merge from committed `689534aaceedd1812d1e783cc063032d1d812158`; no main-checkout WIP, dependencies or generated output was copied. Passive-outpost pages explicitly document Pending/unaccepted status. Local `check:browser` uses Playwright and is NOT RUN under the workstation's CUA Driver-only rule. Build, content, reference, links and explicit-base drift checks remain separate; a browser prerequisite cannot be marked PASS by those checks.

The actual CUA Driver isolated-profile preparation was attempted and refused with `browser_endpoint_owner_mismatch` on 2026-11-05; its owned session was ended. No browser automation fallback was used.

## Local verification

Run from repository root:

```sh
npm ci --prefix wiki --ignore-scripts
npm run test:checks --prefix wiki
npm run check:content --prefix wiki
npm run check:reference --prefix wiki
npm run check:drift --prefix wiki -- --base <explicit-base-commit>
npm run build --prefix wiki
npm run check:links --prefix wiki
npm run check:browser --prefix wiki
```

Browser checks use Playwright Chromium already installed in CI. Locally use WIKI_BROWSER_CHANNEL=chrome (or msedge) for an installed browser. Installing browsers is a separate explicit operation: npx --prefix wiki playwright install chromium. The browser check owns an ephemeral loopback static server and closes it and its browser; it does not use your profile or publish anything. Screenshots are ignored under .test-results/.

For manual reading: npm run preview --prefix wiki -- --host 127.0.0.1; open the printed URL with /rimlink/. Stop the preview afterwards. Do not expose the development server publicly.

## Required update process

Change both ru/<pageId>.md and en/<pageId>.md in the feature task. Preserve pageId, matching contentRevision and explicit shared section anchors. Update content-manifest.json for new pages, translated titles, ownership and sourcePaths; navigation is generated from it. No placeholders or links in lieu of translation. Record unverified behavior honestly, and never add secrets, real tokens or private logs.

check:drift requires an explicit base (PR base SHA, push before SHA or deliberate local merge base). Unmapped src/mod/scripts/deploy changes fail closed: map them to their actual feature articles first. A genuine no-documentation-impact case belongs in docs-impact.json as [{"pageId":"…","sources":["exact/changed/path"],"reason":"Substantial reviewed explanation, at least 30 characters","reviewer":"reviewer identity","base":"explicit-base-commit","after":{"exact/changed/path":"sha256-of-current-bytes"}}]. The exception file itself must change in this diff; base and hashes must match. Rationale is reviewed, never an automatic escape; old exceptions cannot exempt a new edit. Increment contentRevision in the manifest and both translations when updating an existing pair.

reference-inventory.json binds the configuration, HTTP and CLI inventory to source digests. After a contract change inspect the actual Options, binders/validation, route scope and CLI dispatch, update both references, then refresh the corresponding inventory and source digest. A digest-only refresh does not document a change. check:reference compares extracted surfaces, rendered reference coverage and product/protocol versions. Parsers intentionally target this repository's C# declaration style; if that style changes, update parser tests before accepting a new snapshot.

Vite 6.4.5 is pinned via override because the VitePress stable dependency range selected vulnerable Vite 5. Current lockfile audit is clean; verify compatibility, audit, build and browser checks when updating it. No fonts, analytics or search services are fetched externally.

## Publication — separate human decision

wiki-check.yml validates with read-only permissions. Despite its historical filename, wiki-deploy.yml is manual artifact build/check only: no Pages write permission or deploy job. Neither workflow switches hosting or publishes. The active site uses legacy gh-pages:/; do not change its source to Actions.

After explicit publication approval: inspect actual Pages settings and remote gh-pages HEAD; record the prior deployment commit, verify the reviewed source and dist, check Git author/committer and secrets, then copy only dist plus .nojekyll into a separate gh-pages checkout. Commit and push normally without force; do not merge marketing/foreign source changes. Preserve the prior commit for an explicitly approved rollback. Wait for the Pages build, then verify public RU/EN portal, deep links, assets, language/hash and search in a real browser. HTTP200 alone is insufficient. Publishing permission does not permit changing a custom domain or overwriting another site. A different project base requires renewed link/browser checks.

## Legacy documentation

Existing docs and anchors remain in place. Player material maps to ru/players/ and en/players/; server-setup maps to admins/; shared-colony to players/coop; community to players/community; guild-influence to players/guild-influence; procurement to players/procurement; modpack-delivery to players/modpacks and admins/modpacks; client-diagnostics-invites to players/troubleshooting and players/community. Historical plans, specs and verification reports stay repository artifacts, not claims of released functionality.
