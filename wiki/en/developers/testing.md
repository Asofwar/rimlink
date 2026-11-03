---
pageId: developers/testing
contentRevision: 2
---

# Testing and acceptance

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

First reproduce the case and observe a failing targeted test, then make the minimal fix and rerun. Server tests use a real WebApplicationFactory; client-core tests talk to a test server. Full suite: dotnet test Rimlink.sln -nologo -v q.

Open returning is not proof that the peer attached: assert on received events. Measure deadlines with Stopwatch, not DateTime.UtcNow. For timing changes, repeat on Linux/two CPUs where available.

Verify game logic with isolated host/guest instances, separate savedata and a separate server/port. BridgeTools/RimBridge is an available development tool, not a release dependency. Record executed steps/results in the manual checklist; a written scenario alone is not PASS.

Wiki: test:checks → check:content → check:reference → check:drift -- --base &lt;commit&gt; → build → check:links, followed by real browser/search/mobile navigation.

## Which check answers which question? {#test-matrix}

| Check | Establishes | Does not establish |
|---|---|---|
| Solution build/tests | Core/server contracts, storage, boundary scenarios | Game UI/Scribe/pawns |
| Build-ClientMod | Compilation against installed game | A completed two-player flow |
| Two isolated clients | Execution of specified real steps | Every mod/DLC/GOG combination |
| Wiki validators/build | Structure, links, inventory | Semantic article accuracy |
| Browser acceptance | Search/navigation/layout at tested sizes | All browsers or actual OS zoom |

For a bugfix observe RED on the original case, then GREEN. Wait for received stream events, not Open returning. Use Stopwatch deadlines; repeat Linux/two-CPU checks for timing/race changes where available.

## How do I record reproducible acceptance? {#acceptance-record}

Record commit, versions, separate DataDir/port, host/guest savedata and initial state. List actions, expected/observed results, operation IDs, return mail and save/relogin checks. A UI screenshot without server outcome does not prove escrow.

For coop test assigned/unassigned pawns, exit, revocation and rollback. For economy test unknown responses and original replay. Do not operate others' games or clear shared Player.log. Mark unfinished scenarios partial, not PASS.

## Limits and safety {#limits}

Do not start another server against live data or stop someone else's game. Game instances may overwrite the shared Player.log. Never report an unrun check as successful.

## See also {#related}

- [Building the project](./build.md)
- [Contributing and wiki maintenance](./contributing.md)
- [Administrator troubleshooting](../admins/troubleshooting.md)
