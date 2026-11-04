# Lifecycle, retirement и rollback: проверки 2026-11-04

## Область и ограничения

Task13 после [Task12](<2026-11-04-passive-outposts-delivery.md>). Реализация — основная сессия Sol; [lifecycle clarification](<../superpowers/specs/2026-11-04-passive-outposts-lifecycle-clarification.md>) и уточнение [Task13 плана](<../superpowers/plans/2026-11-03-passive-outposts.md#L317-L347>) подготовлены подтверждённой Astra. Эти уточнения не были дополнительным code review. Единственный итоговый обзор всей ветки остаётся Task16.

Production, shared games, секреты и настройки общего Steam не менялись. Capability OFF. Полные G3/G4/G7, ordinary return roundtrip и integrated G5 ещё НЕ пройдены: обязательны в Task16 после зависимостей. Pure/Core/RPC/private GAME/BRIDGE builds не заменяют реальные game evidence. Отсутствие G5 в Task16 означает blocked acceptance, не waiver.

## Реализованные защитные границы

- Общий A → sorted owner O → save S barrier. Полный preflight reset/recreate до первой cancellation/refund/file/generation mutation; отказ последнего owner не меняет остальных.
- Immutable typed lifecycle journal: Prepared → EvidenceRetained → Destructive → Completed. Retain реальных full/delta/base bytes, проверка destination, protected locator, затем source cleanup. Unfinished operation оставляет maintenance без автоматического destructive replay.
- Archive фиксирует единый cut, рассчитывает Q/F и останавливает производство, сохраняя tile/slot/generation/custody. Restore той же колонии возобновляет с now, без archive catchup. Повреждённый protected archive не обнуляется login fallback.
- Reset меняет только положенные mutable projections и generation; старые custody, original proofs, receipts и permanent ordinary negative fences не пересаживаются. Fresh generation требует собственного accepted evidence, а не вечного требования пустой колонии.
- Completed recreate — NoActiveWorld: host/admin Create допустим, gameplay/обычный authority Begin — нет. Последующий WorldBind продолжает installation/chain/boundary и связывает exact target configuration; DB World/authority/head — одно решение, external Pending до него и completion после. Все retired worlds остаются проверяемыми.
- Canonical proof, custody producers и offline-recipient compatibility используют IsCurrent, не highest Number. Active manifest выбирается только в current World binding.
- Protected rollback проверяет actual historical XML/link/local frontier read-only до effects; повышает текущий owner epoch e → e+1 ровно один раз, не откатывает серверный stock и не меняет original XML/epoch/receipts.
- Canonical stream/header/epoch/bridge выбираются одним cut. Authenticated download проверяется до публикации файла. Main-thread bridge меняет только разрешённые mutable scopes, без новых UUID/assignment/Pawn/reference/tombstone; fresh actual snapshot/new ID/current epoch должен быть accepted до следующей mutation. Historical accepted receipt — ACK-only, не spawn/credit/admission.
- LegacySnapshot variant сохраняет допустимость unrelated0.8.1 owner при чужой protected истории. Классификация current namespace за все epochs, actual target/previous IsCurrent bytes и retained locators; global history требует того же protected journal, но не synthetic OutpostSnapshotLink. Exact legacy epoch-gap download даёт null bridge только после проверки committed selection/readiness. Corrupt/tagged/missing-link/unknown target не считается empty history.

## Автоматические RED → GREEN

Все fixtures используют actual accepted XML, SQLite, real authority/anchor и настоящие server/RPC paths, без поддельных successful receipts.

- Archive/restore Q/F, zero-effects preflight, interrupted lifecycle phases, foreign cleanup-owner projection, malformed archive/login fail-closed.
- Reset/recreate: active/prepared/unaccepted return refusal, blocked LAST owner, borrowed tagged-trade cleanup, five lifecycle interruption checkpoints, exact old/next generation.
- WorldBind: four authority Pending checkpoints, target world/config/authority tampering, initial missing/replaced world, two retirements, actual delta+base через archive/restore и reused version number. Corruption любого original retained proof оставляет maintenance.
- Rollback: older selected IsCurrent, unchanged stock/custody/history, rejected incompatible/removal/hash/scope, four interruption points, replay epoch-once; historical admission/delivery/ordinary return acceptance не повторяет импорт или wallet credit; missing/forged bridge/capture/receipt отказывает. До fresh acceptance mutation отказана без side effects, после — выполнена.
- Pure bridge/metadata/download checks: detached all-or-nothing registry plan, foreign/tombstone preservation, wrong owner/world/gen/epoch/boundary/hash/transition/frontier/session refusal. Два real streaming RPC cases проверяют exact old XML и current header.
- Legacy owner: cleanup participant supersedes только свою current projection, real fresh legacy upload, four Pending checkpoints, same operation epoch-once, no fallback на tagged/corrupt target, retained bytes corruption и changed projection download refusal.

## Фактические промежуточные прогоны

Не суммируются: selection sets пересекаются.

- [Server lifecycle/legacy regression](<../../build/outpost-task13-server-regression.log>): 114/114, exit0.
- [Fresh mutation barrier](<../../build/outpost-task13-fresh-mutation-barrier.log>): 1/1, exit0.
- [Client epoch/download/journal regression](<../../build/outpost-task13-client-regression.log>): 113/113, exit0.
- [Expanded legacy + protected rollback](<../../build/outpost-task13-legacy-expanded-green3.log>): 34/34, exit0. Предыдущий RED был реальным отсутствием download refusal; затем исправлен fence, а test expectation уточнён до существующего typed RPC RecoveryRequired, не ослаблен до любого exception.
- Предварительная [private GAME сборка](<../../build/outpost-task13-game-bridge-build2.log>): exit0. Это compile/install proof в собственный stand, не G5.

## Итоговые commit gates

- Первый [полный suite](<../../build/outpost-task13-full-suite.log>): exit1, Bridge5/5 + Server1763/1764 + Discord122/122 + Client722/722 = 2612/2613. Единственный failure — ранее наблюдавшийся Killed_writer_reopens_only_durably_committed_branch(AfterExternalCommit, true): Expected Ready, Actual Maintenance / anchor-storage-unavailable. Это не passing gate и не скрыто успешным повтором.
- [Parallel-host диагностика](<../../build/outpost-task13-owned-exit-diagnostic.log>) локализовала ошибку до первого main-DB HasHistory query: SQLite Error 10 / extended1546 SQLITE_IOERR_TRUNCATE. Оба captured worker-exit факта TRUE: гипотеза ещё работающего собственного child не подтвердилась. Native errno interceptor и сохранение failed fixture добавлены для evidence, не для retry/suppression. Исходная причина Windows/VFS I/O пока НЕ установлена; исправленной её не считаем. Последующие 101-case прогоны, включая три bounded повтора, прошли, но не доказывают устранение intermittent failure.
- Новый [solution build](<../../build/outpost-task13-native-errno-full-build.log>): exit0, 0 errors / 10 предупреждений в ранее существовавших строках. [Полный suite с диагностикой](<../../build/outpost-task13-native-errno-full-suite.log>): exit0, Bridge5/5 + Server1764/1764 + Discord122/122 + Client722/722 = **2613/2613**. Состояние Ready/пустой Reason, отказ на I/O и assertions не ослаблены.
- Финальные [private GAME](<../../build/outpost-task13-final-game.log>) и [BRIDGE](<../../build/outpost-task13-final-bridge.log>): exit0, соответственно 0 errors / 2 и 4 existing warnings. [Readback](<../../build/outpost-task13-private-readback.log>): 8 mod assemblies и 1 companion DLL hash-equal; companion directory clean. Это не реальная integrated G5 acceptance.
- Первый [offline Linux full-suite на двух CPU](<../../build/outpost-task13-linux-suite-1.log>): exit1, Client698/722 + Server1362/1764 + Bridge5/5 + Discord122/122. Основная fixture-причина — отсутствующая explicit QualifiedLinuxFileSystem при actual ext4 mount; второй прогон не запускался из-за fail-fast. Исправлена только test qualification фактического mount в AuthorityHarness/ServerFixture; production defaults/validation не ослаблены.
- [Qualified Linux target](<../../build/outpost-task13-linux-qualified-target.log>): 112/120, exit1 — все восемь failures в новой диагностике worker identity, до recovery: `crash-worker-identity-changed`. Linux `Process.StartTime` переводит kernel ticks через отдельный process-static wall-clock boot time; заменён только diagnostic identity на точный `/proc/pid/stat` field22, Windows creation ticks сохранены. PID, owned-tree kill и Ready/empty-Reason assertions не ослаблены. [Actual kernel-identity target](<../../build/outpost-task13-linux-identity-target.log>): **120/120**, exit0. Оба свежих full two-CPU прогона завершены exit0: [run1](<../../build/outpost-task13-linux-identity-suite-1.log>) и [run2](<../../build/outpost-task13-linux-identity-suite-2.log>) — каждый **2613/2613** (722 + 5 + 122 + 1764), без retries/skips/concurrency reduction. Это проверяет Linux diagnostic fix, не устанавливает причину intermittent Windows1546.
- [Windows full после fixture qualification](<../../build/outpost-task13-qualified-windows-full.log>): exit0, **2613/2613** (5 + 122 + 722 + 1764); [build](<../../build/outpost-task13-qualified-windows-build.log>) exit0, 0 errors / 10 existing warnings. После Linux-only identity correction актуальный [Windows target](<../../build/outpost-task13-current-fixture-target.log>) также прошёл **120/120**, exit0; [current build](<../../build/outpost-task13-current-fixture-build.log>) exit0.
- Task13 implementation/automated gates complete; integrated G5 и единственное итоговое Astra review всей ветки остаются Task16. Неразрешённый intermittent Windows1546 сохранён как ограничение, не объявлен исправленным.
