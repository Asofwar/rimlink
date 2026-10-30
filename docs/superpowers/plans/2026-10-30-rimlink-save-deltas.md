# Rimlink Save Deltas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Old save versions are stored as zstd deltas against a per-player base, so a player's history and the backups take a
fraction of today's disk, while the current version stays a plain full file.

**Architecture:** The form of a version is its file name on disk, not a database column: `<n>.rws.zst` is full, `<n>.d<k>.zst`
is a delta against `base-<k>.rws.zst`. `SaveFiles` (singleton) owns the names, the forms and the store lock; `SaveCompactor` turns
non-current versions into deltas; rollback, the colony archive, the reset path and the backup learn both forms first, and only
the last task wires the compactor into `Store` and the startup pass, so every commit stays green.

**Tech Stack:** .NET 10, ZstdSharp.Port 0.8.8 low-level API (`ZstdSharp.Unsafe.Methods`), EF Core SQLite (no migration), xUnit +
`WebApplicationFactory`, vanilla-JS panel.

**Spec:** `docs/superpowers/specs/2026-10-30-rimlink-save-deltas-design.md`

## Global Constraints

- File names, verbatim: `<n>.rws.zst`, `<n>.d<k>.zst`, `base-<k>.rws.zst`, temp files `upload-*.tmp` and `compact-*.tmp`.
- Constants, not settings, in `SaveDelta`: `DeltaLevel = 9`, `RebaseRatio = 4` (new base when a delta is larger than 1/4 of the
  full file), `MaxRawBytes = 128 * 1024 * 1024`.
- One setting: section `Saves`, class `SaveStorageOptions` in `Hosting/`, key `CompactOldVersions`, default `true`.
- Rebuilt full files use `new Compressor(3)`, like the client. `Size` and `Sha256` always describe the full file.
- No protocol change, no EF migration, no client or mod code except `ClientInfo.Version`. `ProtocolVersion` stays.
- `unsafe` appears only in `Application/Saves/SaveDelta.cs`.
- Unreadable save data surfaces as `InvalidDataException` from `SaveDelta` and `SaveFiles` (wrap `ZstdException`).
- Lock order: `ColonyLocks` then `SaveFiles.PerStoreLock`. `PerStoreLock` is not reentrant: private helpers document that the
  caller holds it.
- Version 0.6.2 in `mod/About/About.xml`, `ClientInfo.Version`, `Directory.Build.props`.
- Targeted test: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~<Class>"`. Before every commit:
  `dotnet test Rimlink.sln -nologo -v q` green.
- Commits: identity `Asofwar <7397608+Asofwar@users.noreply.github.com>` (`git var GIT_AUTHOR_IDENT`), the subject given in each task,
  no trailers. No merge, no push.

## Review Focus

1. An upload for a player while the startup pass works on the same player: the upload waits, no version is lost or left
   unreadable. Test in Task 7: `AnUploadDuringTheSweepLosesNothing`.
2. A download in flight while its version stops being current and gets compacted: the stream finishes with the right bytes, and on
   Windows, where the delete fails, the leftover full file goes with a later pass. Test in Task 3: `ADownloadInFlightFinishes`.
3. A restart in the middle of the first big pass: the next pass continues and redoes nothing harmful. Test in Task 7:
   `ASecondSweepChangesNothing`.
4. Repeated rollbacks mixed with uploads, where a version goes full, delta and full again: every version stays readable. Test in
   Task 4: `RollbacksAndUploadsKeepEveryVersionReadable`.
5. Retention removing the version a base was made from while other deltas still use the base. Test in Task 3:
   `ABaseOutlivesItsVersionAndGoesWithItsLastDelta`.

---

### Task 1: SaveDelta codec

**Files:**
- Create: `src/Rimlink.Server/Application/Saves/SaveDelta.cs`
- Modify: `src/Rimlink.Server/Rimlink.Server.csproj` (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`)
- Test: `tests/Rimlink.Server.Tests/Saves/SaveDeltaTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static unsafe class SaveDelta
  {
      public const int DeltaLevel = 9;
      public const int RebaseRatio = 4;
      public const long MaxRawBytes = 128 * 1024 * 1024;
      public static byte[] Compress(byte[] reference, byte[] content);
      /// <exception cref="InvalidDataException">The delta is damaged, or this is not its reference.</exception>
      public static byte[] Decompress(byte[] reference, byte[] delta);
  }
  ```

- [ ] **Step 1: Write the failing tests** (random data from a seeded `Random`)

  - `NearlyIdenticalContentPacksToAlmostNothing`: 4 MB reference, content is a copy with 1000 bytes changed:
    `Assert.True(delta.Length < 20_000)`, `Assert.Equal(content, SaveDelta.Decompress(reference, delta))`.
  - `UnrelatedContentRoundTrips`: two different 1 MB arrays.
  - `ContentLargerThanTheReferenceRoundTrips`: 1 MB reference, 3 MB content starting with it.
  - `ADamagedDeltaIsInvalidData`: flip a byte in the middle: `Assert.Throws<InvalidDataException>`.
  - `ATruncatedDeltaIsInvalidData`: drop the last 10 bytes: same.
  - `TheWrongReferenceIsInvalidData`: decompress with another array of the same length: same.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveDeltaTests"`
  Expected: build error, `SaveDelta` does not exist.

- [ ] **Step 3: Implement `SaveDelta`**

  Working calls, proven by a spike against real saves. `Compress`: `ZSTD_createCCtx`, `ZSTD_CCtx_setParameter` with
  `ZSTD_c_compressionLevel` = `DeltaLevel`, `ZSTD_c_windowLog` = `Math.Max(23, 1 + (int)Math.Ceiling(Math.Log2(Math.Max(reference.Length, content.Length))))`,
  `ZSTD_c_enableLongDistanceMatching` = 1, `ZSTD_c_checksumFlag` = 1, then `ZSTD_CCtx_refPrefix` and `ZSTD_compress2` into a
  `ZSTD_compressBound` buffer, `ZSTD_freeCCtx` in `finally`. `Decompress`: read the size with `ZSTD_getFrameContentSize` (unknown,
  error or above `MaxRawBytes` is `InvalidDataException`), `ZSTD_createDCtx`, `ZSTD_d_windowLogMax` = 31, `ZSTD_DCtx_refPrefix`,
  `ZSTD_decompressDCtx`, `ZSTD_freeDCtx`. Every result goes through a `Check(nuint)` on `ZSTD_isError` that throws
  `InvalidDataException(ZSTD_getErrorName(result))`. A prefix lasts one frame, so a context is never reused.

- [ ] **Step 4: Run to verify they pass**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveDeltaTests"`
  Expected: 6 passed.

- [ ] **Step 5: Commit** — `"Add a reference-prefix zstd codec for save deltas"`.

### Task 2: SaveFiles

**Files:**
- Create: `src/Rimlink.Server/Application/Saves/SaveFiles.cs`
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs`, `src/Rimlink.Server/Application/Saves/SaveArchive.cs`, `src/Rimlink.Server/Program.cs`
- Test: `tests/Rimlink.Server.Tests/Saves/SaveFilesTests.cs`

**Interfaces:**
- Consumes: `SaveDelta` (Task 1).
- Produces:
  ```csharp
  public abstract record SaveForm
  {
      public sealed record Full(string Path) : SaveForm;
      public sealed record Delta(string Path, int BaseNumber, string BasePath) : SaveForm;
      public sealed record Missing : SaveForm;
  }

  public sealed class SaveFiles(DataDirectory data)
  {
      public SemaphoreSlim PerStoreLock { get; }             // moved out of SaveStore
      public string FullPath(long playerId, int number);
      public string DeltaPath(long playerId, int number, int baseNumber);
      public string BasePath(long playerId, int baseNumber);
      public SaveForm Locate(long playerId, int number);     // spec §3
      public int? NewestBase(long playerId);
      public byte[]? ReadRaw(string path);                   // null above MaxRawBytes
      public (long Size, byte[] Sha256) Rebuild(SaveForm.Delta delta, string target);
      public void WriteAtomically(string target, ReadOnlySpan<byte> content);
      public void DeleteDeltas(long playerId, int number);
      public void Delete(long playerId, int number);         // every form of the version
      public void PruneBases(long playerId);                 // bases no delta in the directory names
      public long DiskBytes();                               // size of saves/, a file gone mid-count is skipped
  }
  ```

- [ ] **Step 1: Write the failing tests** (a `DataDirectory` on a temp folder, deleted in `Dispose`)

  - `LocatePrefersTheFullFile`: `1.rws.zst`, `base-1.rws.zst`, `1.d1.zst`: `Assert.IsType<SaveForm.Full>`.
  - `LocateFindsADeltaWithItsBase`: `2.d1.zst`, `base-1.rws.zst`: `SaveForm.Delta` with `BaseNumber == 1`.
  - `ADeltaWithoutItsBaseIsMissing`.
  - `LocateDoesNotConfuseNumbers`: only `11.d3.zst` and `base-3.rws.zst`: `Locate(1)` is `Missing`.
  - `DeleteRemovesEveryForm`: `1.rws.zst` and `1.d1.zst` are gone, `11.d1.zst` stays.
  - `PruneKeepsBasesThatADeltaNames`: `base-1` with `2.d1.zst` stays, `base-3` goes.
  - `NewestBaseIsTheHighestNumber`: `base-2`, `base-10`: 10; empty directory: null.
  - `RebuildRestoresTheContent`: base and delta from `SaveFixtures`, `Rebuild` returns the target's length and SHA-256, `ReadRaw(target)`
    equals the content, the target's `.tmp` neighbour is gone.
  - `RebuildOfADamagedDeltaLeavesNoTarget`: `InvalidDataException`, `File.Exists(target)` false.
  - `ReadRawOfAnUnreadableFileIsInvalidData`: file `[1, 2, 3]`.
  - `ReadRawIsNullAboveTheLimit`: `Compress(new byte[SaveDelta.MaxRawBytes + 1])`.
  - `DiskBytesSumsTheTree`: two files in two player directories.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveFilesTests"`
  Expected: build error, `SaveFiles` does not exist.

- [ ] **Step 3: Implement `SaveFiles`; register it as a singleton in `Program.cs`**

  `WriteAtomically`: `compact-<guid>.tmp` beside the target, `FileStream.Flush(flushToDisk: true)`, `File.Move(overwrite: true)`, temp removed in
  `finally`. `ReadRaw`: `DecompressionStream` copied with a cap, `ZstdException` becomes `InvalidDataException`. `Rebuild`: read base and delta,
  `SaveDelta.Decompress`, `new Compressor(3)`, `WriteAtomically`, hash the bytes.

- [ ] **Step 4: Move `SaveStore` and `SaveArchive` onto it**

  `SaveStore`: inject `SaveFiles`, drop the static `PerStoreLock` and `PathOf`. `SaveArchive`: inject `SaveFiles`, drop `PathOf`; `CopyIn` writes to `FullPath`.
  Behaviour does not change in this step.

- [ ] **Step 5: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 6: Commit** — `"Move save file names and the store lock into SaveFiles"`.

### Task 3: SaveCompactor

**Files:**
- Create: `src/Rimlink.Server/Application/Saves/SaveCompactor.cs`, `src/Rimlink.Server/Hosting/SaveStorageOptions.cs`
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`ApplyRetention`), `src/Rimlink.Server/Program.cs`, `deploy/appsettings.example.json`, `tests/Rimlink.Server.Tests/Support/SaveFixtures.cs`
- Test: `tests/Rimlink.Server.Tests/Saves/SaveCompactionTests.cs`

**Interfaces:**
- Consumes: `SaveDelta`, `SaveFiles`, `SaveForm`.
- Produces:
  ```csharp
  public sealed class SaveCompactor(RimlinkDbContext db, SaveFiles files, IOptions<SaveStorageOptions> options, ILogger<SaveCompactor> log)
  {
      /// <summary>Spec §4.2. The caller holds SaveFiles.PerStoreLock.</summary>
      public Task Run(long playerId);
  }
  public sealed class SaveStorageOptions { public const string Section = "Saves"; public bool CompactOldVersions { get; set; } = true; }
  ```
  In `SaveFixtures`: `public static async Task Compact(ServerFixture fixture, long playerId)` takes `PerStoreLock` and calls `Run` in a scope. Nothing calls
  `Run` in production until Task 7, so tests of Tasks 3–6 call this after their uploads; once `Store` calls `Run` it changes nothing.

The fixture's padding is random but seeded by its size: two saves with the same `paddingBytes` are nearly identical, two with different
sizes share nothing. The tests rely on that. Helpers in the test class: `Dir(fixture, playerId)`, `Exists(fixture, playerId, name)`,
`Send(fixture, login, tick, padding)` returning the uploaded bytes and calling `Compact`.

- [ ] **Step 1: Write the failing tests**

  - `AnOldVersionBecomesADeltaAndTheCurrentStaysFull`: ticks 1, 2, padding 1_000_000: `2.rws.zst`, `1.d1.zst`, `base-1.rws.zst` exist, `1.rws.zst` does not,
    `new FileInfo(1.d1.zst).Length < 10_000`, `Download` equals the second upload.
  - `LaterVersionsShareTheBase`: three uploads: `1.d1.zst`, `2.d1.zst`, `3.rws.zst`, one `base-*`.
  - `ADeltaPastTheRatioStartsANewBase`: paddings 1 MB, 1 MB, 2 MB, 2 MB: `base-1` and `base-3` exist, `3.d3.zst` exists, `2.d1.zst` still decodes
    against `base-1` to the second upload's raw content.
  - `ABaseOutlivesItsVersionAndGoesWithItsLastDelta`: v1 padding 200_000, v2–v13 padding 400_000: nothing named `1.*` or `2.*` remains, `base-1` is gone,
    `base-2` stays, `Locate(4)` is `SaveForm.Delta` with `BaseNumber == 2`.
  - `RetentionStillPrunesBasesWhenCompactionIsOff`: fixture with `KeepDataDir()` uploads v1, v2 (padding 200_000), restart with
    `WithSetting("Saves:CompactOldVersions", "false").WithDataDir(dir).WithoutWorld()`, 12 more uploads: `base-1` is gone.
  - `AVersionOverTheRawLimitStaysFull`: v1 is `MakeSave` with `SaveDelta.MaxRawBytes + 1` spaces spliced before `</game>`, recompressed; v2 is plain:
    `1.rws.zst` stays, no `base-*`.
  - `ADamagedBaseIsReplaced`: v1, v2, then `base-1.rws.zst` overwritten with `[1, 2, 3]`, v3: `base-2.rws.zst` and `2.d2.zst` exist.
  - `AStaleDeltaIsRedoneByTheNextPass`: v1, v2, garbage in `2.d1.zst`, v3: `2.d1.zst` decodes to the second upload's raw content.
  - `TheCurrentVersionLosesAStaleDelta`: v1, v2, garbage in `2.d1.zst`, `Compact`: the file is gone, `2.rws.zst` is unchanged.
  - `ABaseWithoutDeltasGoesWithTheNextPass`: stray `base-9.rws.zst`, then `Compact`: it is gone.
  - `CompactionOffLeavesFullFiles`: `Saves:CompactOldVersions=false`, two uploads: both `*.rws.zst`, no `base-*`.
  - `ADownloadInFlightFinishes`: `SaveStore.OpenCurrent` in a scope, v2, read the open stream to the end: equals v1; v3: `1.rws.zst` is gone.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveCompactionTests"`
  Expected: build error, `SaveCompactor` does not exist.

- [ ] **Step 3: Add `SaveStorageOptions`**

  `builder.Services.Configure<SaveStorageOptions>(builder.Configuration.GetSection(SaveStorageOptions.Section))` in `Program.cs`, `AddScoped<SaveCompactor>()`, and
  `"Saves": { "CompactOldVersions": true }` in `deploy/appsettings.example.json`.

- [ ] **Step 4: Implement `SaveCompactor.Run`**

  Follow spec §4.2 steps in order; `CompactOldVersions = false` returns at once. A base that throws `InvalidDataException` or is above the limit counts as no base.
  A failure on one version (`InvalidDataException`, `IOException`) is logged and the loop goes on. Deleting the full file in step 7 catches `IOException` and only logs at debug level.
  Log per version: `"Compacted save v{Version} of player {PlayerId}: {Full} -> {Delta} bytes against base {Base}"`. Compare the delta with the full file's length: `delta.Length * SaveDelta.RebaseRatio > fullLength`.

- [ ] **Step 5: Teach retention both forms**

  `ApplyRetention`: `files.Delete(playerId, old.Number)` for each version, `files.PruneBases(playerId)` once at the end.

- [ ] **Step 6: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 7: Commit** — `"Add the compactor that turns old save versions into deltas"`.

### Task 4: Reading a delta back

**Files:**
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`OpenCurrent`, `Rollback`, new `EnsureFull`)
- Test: `tests/Rimlink.Server.Tests/Saves/SaveCompactionTests.cs`

**Interfaces:**
- Consumes: `SaveFiles.Locate`, `SaveFiles.Rebuild`, `SaveCompactor.Run`.
- Produces: `private async Task<bool> EnsureFull(SaveVersion version)` in `SaveStore`: true when the full file exists afterwards, false when the version cannot be
  read; updates `Size` and `Sha256` on the object and in the row, then deletes the delta. The caller holds `PerStoreLock`.

- [ ] **Step 1: Write the failing tests**

  Uploads here go through `Send`, which compacts.

  - `RollbackToADeltaRestoresTheContent`: v1, v2, `Rollback(1)` true: `Decompress(Download.data)` equals `Decompress(first)`, `1.rws.zst` exists, `1.d1.zst` is gone, `2.d1.zst` exists,
    `2.rws.zst` is gone, the row's `Size` and `Sha256` match the file on disk.
  - `RollbackToADamagedDeltaIsRefused`: truncate `1.d1.zst`: `Rollback(1)` false, `Download` still equals v2.
  - `RollbackWithoutTheBaseIsRefused`: delete `base-1.rws.zst`: same.
  - `DownloadRebuildsACurrentVersionLeftAsADelta`: v1, v2, then `ExecuteUpdate` makes v1 current and v2 not (a backup's database with newer files): `Decompress(Download.data)` equals `Decompress(first)`,
    `1.rws.zst` exists, `header.TotalSize` equals its length.
  - `RollbackWorksAfterCompactionWasTurnedOff`: restart over the data directory with `Saves:CompactOldVersions=false`: `Rollback(1)` true, content equal.
  - `RollbacksAndUploadsKeepEveryVersionReadable`: v1, v2, v3, `Rollback(1)`, v4, `Rollback(2)`, v5; then for each number `Rollback(n)` and `Download`: content equals what was uploaded as that version.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveCompactionTests"`
  Expected: four fail, `Rollback` returns false on a delta. The two refusal tests already pass and pin that behaviour through the change.

- [ ] **Step 3: Implement `EnsureFull` and `Rollback`**

  Spec §5.1 and §5.2. `SaveStore`'s constructor gains `SaveCompactor compactor`. `Rollback` takes `PerStoreLock` around the whole method, calls `EnsureFull` on `target` first and returns false when it fails (logged as an error), then does what it does today, then runs the compactor in a `try` that only logs a failure. `InvalidDataException` and `IOException` from `Rebuild` become false.

- [ ] **Step 4: Implement `OpenCurrent`**

  Fast path as today. When the full file is missing, take `PerStoreLock`, `EnsureFull`, release, then open. Wrap in two attempts: a `FileNotFoundException` on the first re-reads the row (spec §5.1).

- [ ] **Step 5: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 6: Commit** — `"Rebuild full files for rollbacks and a current version left as a delta"`.

### Task 5: Archives

**Files:**
- Modify: `src/Rimlink.Server/Application/Saves/SaveArchive.cs` (`EnsureFilesExist`, `CopyOut`, `DeleteFiles`), `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`Archive`)
- Test: `tests/Rimlink.Server.Tests/World/ColonyArchiveTests.cs`, `tests/Rimlink.Server.Tests/Saves/SaveCompactionTests.cs`

**Interfaces:**
- Consumes: `SaveFiles.Locate`, `Rebuild`, `Delete`, `PruneBases`.
- Produces: no new surface; `ArchiveOutcome` and `SaveStore.Archive(long, string)` keep their shapes. The archive format does not change.

- [ ] **Step 1: Write the failing tests in `ColonyArchiveTests`**

  Each test calls `Compact` after `Settled` and after any upload.

  - `ArchiveWritesDeltasAsFullFiles`: `Settled(uploads: 3)`, `Archive`: three `*.rws.zst` in the archive, nothing else but `manifest.json`, the manifest's `Size` and `Sha256` match each file, `Decompress(file)` equals each upload's raw content, the player's save directory holds no files.
  - `RestoreOfDeltasBringsBackTheContent`: `Settled(uploads: 2)`, `Archive`, `Restore`: `Rollback(1)` then `Download` equals the first upload's raw content.
  - `TheNextUploadCompactsARestoredColony`: after that restore, one upload: `1.rws.zst` is gone, a `1.d*.zst` exists.
  - `ArchiveIsRefusedWhenADeltaLostItsBase`: delete `base-*`: `ThrowsAnyAsync`, rows and settlement stay.
  - `ArchiveIsRefusedWhenADeltaIsDamaged`: truncate `1.d1.zst`: same.

- [ ] **Step 2: Write the failing tests in `SaveCompactionTests`** (calling `SaveStore.Archive` directly)

  - `ResetLeavesFullFilesAndAnEmptyPlayerDirectory`: v1, v2, v3: three `*.rws.zst` in the target, contents equal, the player's directory holds no files.
  - `ResetMovesADamagedDeltaWithItsBase`: truncate `1.d1.zst`: target holds `1.d1.zst`, `base-1.rws.zst`, `2.rws.zst`, `3.rws.zst`, the player's directory holds no files.
  - `NumbersStartOverCleanlyAfterAReset`: after `Archive`, an upload is version 1, `Download` equals it.

- [ ] **Step 3: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~ColonyArchiveTests|FullyQualifiedName~SaveCompactionTests"`
  Expected: `ArchiveWritesDeltasAsFullFiles`, `RestoreOfDeltasBringsBackTheContent`, `TheNextUploadCompactsARestoredColony`, `ResetLeavesFullFilesAndAnEmptyPlayerDirectory` and
  `ResetMovesADamagedDeltaWithItsBase` fail. The other three already pass and pin that behaviour through the change.

- [ ] **Step 4: Implement `SaveArchive`**

  Spec §5.4. `EnsureFilesExist` throws on `SaveForm.Missing`. `CopyOut` keeps today's copy and hash check for `Full`; for `Delta`, `Rebuild` into the archive and the manifest takes the returned size and hash; for `Missing`, `FileNotFoundException`. `DeleteFiles` calls `files.Delete` per number and `PruneBases` at the end.

- [ ] **Step 5: Implement `SaveStore.Archive`**

  Spec §5.5, under `PerStoreLock`. For a delta that cannot be rebuilt: log an error, move the delta, copy its base to the target. After the loop every version gets `files.Delete`, then `PruneBases`.

- [ ] **Step 6: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 7: Commit** — `"Write full files when a colony or a player's saves are archived"`.

### Task 6: Backup

**Files:**
- Modify: `src/Rimlink.Server/Backup/BackupArchive.cs` (`Create`)
- Test: `tests/Rimlink.Server.Tests/Tools/BackupArchiveTests.cs`, `tests/Rimlink.Server.Tests/Saves/SaveCompactionTests.cs`

**Interfaces:**
- Produces: `public static void Create(string dataDir, string outFile, Action<string>? beforeEntry = null)`; the callback gets the entry's relative path and exists for tests. `BackupCommands` and `BackupService` keep calling it with two arguments.

- [ ] **Step 1: Write the failing tests**

  In `BackupArchiveTests`, on `MakeDataDir` with extra files in `saves/7`:
  - `BackupCarriesDeltasAndBases`: `4.d1.zst`, `base-1.rws.zst` come back from `Restore` with their content.
  - `TemporaryFilesAreNotPacked`: `upload-x.tmp`, `compact-x.tmp` do not come back.
  - `AFileGoneDuringPackingIsSkipped`: `3.rws.zst` and `4.rws.zst`; `beforeEntry` deletes the other one on its first call: `Create` does not throw, exactly one comes back.
  - `AFileAddedDuringPackingIsCaughtByTheSecondPass`: `beforeEntry` writes `5.d1.zst` on its first call: it comes back.

  In `SaveCompactionTests`:
  - `AServerRestoredFromABackupReadsItsDeltas`: v1, v2, v3 through `Send`, `BackupArchive.Create(fixture.DataDir, file)` after disposing the fixture with `KeepDataDir()`, `Restore` into a new directory, a fixture with `WithDataDir(restored).WithoutWorld()`: `Rollback(1)` true, `Download` content equals v1.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~BackupArchiveTests"`
  Expected: build error, `Create` has no `beforeEntry`.

- [ ] **Step 3: Implement**

  Spec §6. Two passes over `Directory.EnumerateFiles`, a `HashSet<string>` of relative paths already taken, `*.tmp` skipped, `tar.WriteEntry` in a `try` that swallows `FileNotFoundException` and `DirectoryNotFoundException`. One comment line on why there are two passes.

- [ ] **Step 4: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 5: Commit** — `"Let a backup survive save files changing while it packs"`.

### Task 7: Wiring, startup pass, status, documentation

**Files:**
- Modify: `src/Rimlink.Server/Application/Saves/SaveStore.cs` (`Store`), `src/Rimlink.Server/Application/Saves/SaveCompactor.cs` (`SweepAll`, `SaveCompactionService`), `src/Rimlink.Server/Program.cs`, `src/Rimlink.Server/Application/Admin/ServerStatus.cs`, `src/Rimlink.Server/wwwroot/admin/views/overview.js:27`
- Modify docs: `docs/server-setup.md`, `AGENTS.md`, `docs/manual-checklist.md`
- Modify version: `mod/About/About.xml`, `src/Rimlink.Client.Core/ClientInfo.cs`, `Directory.Build.props`
- Test: `tests/Rimlink.Server.Tests/Saves/SaveCompactionTests.cs`, `tests/Rimlink.Server.Tests/Admin/StatusConfigTests.cs`

**Interfaces:**
- Consumes: `SaveCompactor.Run`, `ColonyLocks.Hold`, `SaveFiles.PerStoreLock`, `SaveFiles.DiskBytes`.
- Produces: `public Task SweepAll(CancellationToken cancellationToken)` on `SaveCompactor`, whose constructor gains `ColonyLocks locks`; `SaveCompactionService(IServiceScopeFactory, ILogger<SaveCompactionService>) : BackgroundService`, registered with `AddHostedService`.

- [ ] **Step 1: Write the failing tests**

  In `SaveCompactionTests`, with plain `Upload`, no `Compact`:
  - `AnUploadCompactsTheVersionItReplaces`: two uploads: `1.d1.zst` and `2.rws.zst` exist, `1.rws.zst` does not.
  - `AnUploadStillSucceedsOverADamagedBase`: v1, v2, `base-1.rws.zst` overwritten with `[1, 2, 3]`: the third upload returns version 3.
  - `TheStartupPassCompactsVersionsFromBeforeTheUpdate`: three uploads under `Saves:CompactOldVersions=false` and `KeepDataDir()`, restart with the default: `Eventually` `1.d1.zst` and `2.d1.zst` exist, `3.rws.zst` stays, `Download` equals v3.
  - `TheSweepDoesNothingWhenCompactionIsOff`: same arrangement with the restart off, `SweepAll` in a scope: `1.rws.zst` stays.
  - `ASecondSweepChangesNothing`: after a sweep, the directory's names and bytes are equal before and after another `SweepAll`.
  - `AnUploadDuringTheSweepLosesNothing`: five full versions under `Saves:CompactOldVersions=false`, restart on, `Task.WhenAll(SweepAll, Upload v6)`: `Locate` of 1–6 is never `Missing`, `Download` equals v6, each version's content is right after `Rollback`.

  In `StatusConfigTests`: after one upload, `status.saves.diskBytes` equals the size of `1.rws.zst`; `/config` has `saves.compactOldVersions == true`.

- [ ] **Step 2: Run to verify they fail**

  Run: `dotnet test tests/Rimlink.Server.Tests -nologo -v q --filter "FullyQualifiedName~SaveCompactionTests|FullyQualifiedName~StatusConfigTests"`
  Expected: build error, `SweepAll` does not exist.

- [ ] **Step 3: Call the compactor from `Store`**

  After the `integrity.OnStored` block and still inside the lock: `try { await compactor.Run(playerId); } catch (Exception ex) { log.LogError(...); }`.

- [ ] **Step 4: Implement the pass**

  Spec §4.4. `SweepAll`: returns at once when off; player ids from non-current rows whose `FullPath` exists; per player `locks.Hold`, then `PerStoreLock`, `Run`, release both; stops on cancellation. `SaveCompactionService.ExecuteAsync`: one scope, one `SweepAll`, errors logged, then it ends.

- [ ] **Step 5: Implement status and config**

  `ServerStatus`: inject `SaveFiles` and `IOptions<SaveStorageOptions>`; `saves` gains `diskBytes = files.DiskBytes()`, `Config()` gains `["saves"]`. `overview.js:27` shows `status.saves.diskBytes`.

- [ ] **Step 6: Run the whole suite**

  Run: `dotnet test Rimlink.sln -nologo -v q`
  Expected: all green.

- [ ] **Step 7: Documentation**

  - `docs/server-setup.md`: the data layout line gets the three file forms; `Saves:CompactOldVersions`; a note that returning to 0.6.1 keeps the current versions and refuses rollbacks to compacted ones.
  - `AGENTS.md`, section "Rules that are not obvious": save files have two forms and the form is the file name; only `SaveFiles` knows the names; nothing assumes `<n>.rws.zst` exists for a non-current version.
  - `docs/manual-checklist.md`: unticked item for the live server: `saves/` shrank, rollback from the panel to a compacted version works, the player joins on it, a backup restores into a separate directory and a server starts on it.

- [ ] **Step 8: Commit** — `"Compact old save versions on upload and at startup"`.

- [ ] **Step 9: Bump the version**

  0.6.2 in `mod/About/About.xml`, `src/Rimlink.Client.Core/ClientInfo.cs`, `Directory.Build.props`. Run `dotnet test Rimlink.sln -nologo -v q`: all green. Build the mod: `.\scripts\Build-ClientMod.ps1 -RimWorldDir 'D:\SteamLibrary\steamapps\common\RimWorld'`: it compiles.

- [ ] **Step 10: Commit** — `"Bump the version to 0.6.2"`.
