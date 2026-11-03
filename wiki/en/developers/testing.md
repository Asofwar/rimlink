---
pageId: developers/testing
contentRevision: 1
---

# Testing and acceptance

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

First reproduce the case and observe a failing targeted test, then make the minimal fix and rerun. Server tests use a real WebApplicationFactory; client-core tests talk to a test server. Full suite: dotnet test Rimlink.sln -nologo -v q.

Open returning is not proof that the peer attached: assert on received events. Measure deadlines with Stopwatch, not DateTime.UtcNow. For timing changes, repeat on Linux/two CPUs where available.

Verify game logic with isolated host/guest instances, separate savedata and a separate server/port. BridgeTools/RimBridge is an available development tool, not a release dependency. Record executed steps/results in the manual checklist; a written scenario alone is not PASS.

Wiki: test:checks → check:content → check:reference → check:drift -- --base &lt;commit&gt; → build → check:links, followed by real browser/search/mobile navigation.

## Limits and safety {#limits}

Do not start another server against live data or stop someone else's game. Game instances may overwrite the shared Player.log. Never report an unrun check as successful.

## See also {#related}

- [Building the project](./build.md)
- [Contributing and wiki maintenance](./contributing.md)
- [Administrator troubleshooting](../admins/troubleshooting.md)
