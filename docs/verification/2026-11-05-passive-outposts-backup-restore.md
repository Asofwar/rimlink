# Passive outposts — Task14 backup / restore verification

Date: 2026-11-05. Isolated branch: passive-outposts; base: 505d40dd0e2ba6ecbfc08ba0b5a255786db88743.

## Status

Task14 implementation and automated gates are complete. **G6 remains pending Task16.** Outpost capability remains OFF.
No production data, user backups, shared games, release, deployment, merge or push were used.
The required real isolated G6 game scenario remains in Task16 alongside G1–G7 and the single final Astra review.

Authority: [accepted Task14 clarification](<../superpowers/specs/2026-11-04-passive-outposts-bulk-restore-clarification.md>)
and [implementation plan, Task14](<../superpowers/plans/2026-11-03-passive-outposts.md#task-14-consistent-backup-и-recoverable-stopped-server-restore>).

## Implemented boundaries

- Online REST/scheduler and explicitly stopped-server CLI use the same coherent authority-aware capture:
  A → sorted owners → save barrier → live barrier → transaction, one settlement clock and one DB/files/reference cut.
  Required full/delta/base, archived/retired sources, immutable history and negative fences are retained and hashed;
  the external authority database is never archive payload. Proven no-history legacy entrypoints remain available.
- Inspection is bounded and read-only: archive path/link/alias/duplicate/size checks, exact inventory and real evidence
  closure, separate cut C and actual external E. It does not create the target data directory, initialize history,
  provision authority, recover an external hot journal or treat an unknown old authority as lost.
- Existing-installation, import and lost-anchor modes freeze the exact selection. Import requires a positively empty
  destination and separately provisioned fresh authority; lost-anchor recovery independently observes the actual known
  previous location. Every destructive execution needs a separate typed digest confirmation.
- The durable external journal precedes replacements. The main immutable history is C → typed BulkRestore R;
  the distinct external publication is E → committed RestoreInProgress marker → verified R/completion descendant.
  Per-file planned/effect/completed boundaries, staged bytes and a verified retained old set survive interruption.
- Explicit same-id Resume completes only proven recorded work. Confirmed backout is a separate action, permitted only
  before external publication and only to a verified old set (or proven original empty import destination).
  Normal startup never automatically continues an unfinished destructive operation.
- Stock/fraction are conserved; active production across the restore/completion gap is zero, archived outposts stay
  paused, and ordinary downtime production resumes only after a proven completed publication.
- Additive restore/source provenance authenticates nested restore/rollback downloads and exact original bytes.
  Main-thread load changes only authorized mutable outer/live-own bindings; UUID, assignment, reference, tombstone,
  foreign observations, original captures/receipts and stale pending requests are not rebased. Mutation requires a
  fresh actual accepted snapshot. Proven unrelated legacy saves need no fabricated protected snapshot link.
- Pending parcels retain their original ID/payload. Typed current-boundary authorization joins immutable R/cut/maps;
  later epochs require an exact validated rollback chain, not an epoch-range allowance. Current delivery starts only
  after fresh acceptance. Canonical accepted receipt is ACK-only; custody/credit/materialization are not imported twice.
- CLI provides read-only inspect/digest/backout-inspect/backout-digest and separately confirmed execute/backout plus
  exact-id resume. Embedded request confirmation alone is never permission. See the [operator runbook](<../server-setup.md#protected-outpost-backup-and-recovery>).

## Observed tests and corrections

Only the commands and outputs listed below are claimed; earlier targeted checkpoints are not an aggregate full gate.
All logs are local git-ignored build artifacts, not shipped deliverables.

- Actual restore-delivery RPC: 2/2 PASS, including restore → rollback → exact download → fresh real upload →
  original parcel → current Begin/replay → accepted receipt → ACK-only / second accepted save.
- Qualified server delivery/negative regression: 25/25 PASS. Missing/changed authorization, old/relabelled physical
  proof, wrong payload/owner and unaccepted snapshots refuse without an attempt or domain mutation.
- CLI + snapshot qualification: 47/47 PASS. This includes 10 CLI cases and 37 snapshot cases.
- Explicit two-retired-worlds / original archived delta+base qualification: 2/2 PASS on Windows. Valid backup →
  inspect → restore preserves original links/bytes; removing a required original base refuses even with a recomputed
  inventory, without destination/domain mutation. The original archive locator is checked, not a later full-copy locator.
- All client outpost tests after explicit nullable guards: 404/404 PASS. New source/load/download nullable warnings
  were removed without null-forgiving operators or relaxed provenance validation.
- Real owned-process hard-kill restore + existing authority qualification: 25/25 PASS. It bypasses procedure
  finally/Dispose at fifteen restore checkpoint boundaries; existing authority crash cases passed in the same selection. This tests process
  interruption and recovery ordering, not arbitrary hardware power-loss durability.
- Final Windows solution build: PASS (0 errors, 2 existing nullable test warnings). Full solution: **2983/2983 PASS**
  (Server 2010, Client 846, Discord 122, Bridge 5; [Windows full log](<../../build/outpost-task14-final-suite.log>)).
  The earlier full test run was interrupted after seven old
  snapshot fixture failures; it is **not** a passing gate. Their actual cause was a fixture storing UTF-8 text instead
  of the real serialized BeginDelivery request with its exact hash. The fixture now stores the production contract;
  36/36 original snapshot tests passed. A further real RED proved malformed protobuf escaped as an untyped exception;
  the guard now returns a typed evidence refusal, with no attempt committed and authority still Ready.
- Linux hard-kill qualification: 25/25 PASS on two CPUs after fixing a test-worker reap defect. The initial target
  failed fifteen restore cases because launcher-first termination left the already dead owned worker in Linux zombie
  state Z. The fixture now hard-kills and awaits the positively identified worker while its launcher can reap it,
  then terminates the launcher. The strict exit assertions remain unchanged; no zombie-as-success or sleep was added.
  Current fresh-copy target: **283/283 PASS** ([target log](<../../build/outpost-task14-linux-final-target.log>));
  both two-CPU full suites: **2983/2983 PASS each** ([full run 1](<../../build/outpost-task14-linux-final-suite-1.log>),
  [full run 2](<../../build/outpost-task14-linux-final-suite-2.log>)); the runner completed with exit 0.
  The previous copy completed 2981/2983 with two stale history-fixture failures:
  it selected a later full-copy locator rather than the required original archive delta/base. That failed run is not
  a gate and was not concealed; the new copy includes the corrected original-locator and exact-refusal assertions.
- A later Windows full gate was interrupted after an AfterRestorePublication Ready-vs-Maintenance failure.
  Its cause is not established. A strengthened diagnostic retains the failed fixture and native OS error;
  the isolated publication case subsequently passed 1/1, which is not evidence that the intermittent fault is fixed.
- Private GAME + BRIDGE: final build/install PASS, independently read back SHA-256 for all eight mod assemblies
  and the single companion. No new source/load/download nullable warnings remained. This is not game acceptance.

## Restrictions and known uncertainty

The previously observed native Windows SQLITE_IOERR_TRUNCATE (extended code 1546) has **not** been root-caused or
claimed fixed. Prior diagnostics refuted a still-running owned worker; native failure capture remains available.
A later successful run is not proof that this intermittent I/O condition was repaired. No retry, assertion suppression,
skip, concurrency reduction, SQLite provider/VFS replacement or unknown-process termination was used to conceal it.

G6, all Task16 game acceptance, the agreed release-number bump and the single final whole-branch Astra review remain
outstanding. No claim in this report enables production capability or authorizes publication.
