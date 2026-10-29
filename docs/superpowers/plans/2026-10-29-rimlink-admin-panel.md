# Rimlink Admin Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The owner runs the whole server from a browser (`/admin/`) and players see a public status page (`/`), both served by `Rimlink.Server` itself over the same REST API the CLI and the future Discord bot use.

**Architecture:** Static ES-module pages in `src/Rimlink.Server/wwwroot` (no build step) talk to `/api/admin/*` with a cookie session obtained by entering `Admin:ApiToken`; the existing Bearer path stays for CLI and bot. New read services (`AdminDirectory`, `ServerStatus`, `BackupService`) and a few new routes fill the gaps (player directory, card, chat, status, config, backups, public status); every mutation still goes through `AdminOperations` with its audit, which now records the actor's name.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs + static files, EF Core SQLite (one migration), xUnit + `WebApplicationFactory`, vanilla JS (ES modules), CSS.

**Spec:** `docs/superpowers/specs/2026-10-29-rimlink-admin-panel-design.md`

## Global Constraints

- `.proto` files, `ProtocolVersion.Current`, `src/Rimlink.Client*`, `mod/` do not change. Existing REST routes keep their paths and response shapes; fields are only added.
- No new NuGet packages, no Node/npm, no CDN. The only external URLs in `wwwroot` are Workshop links built from a numeric id: `https://steamcommunity.com/sharedfiles/filedetails/?id=<id>`.
- Exact values (spec §2–§4):
  - cookie `rimlink_panel`; panel header `X-Rimlink-Panel: 1`; actor header `X-Rimlink-Actor`, label ≤ 64 chars;
  - actor names `api`, `panel`, `api:<label>`;
  - at most 20 sessions; 5 failed logins per IP per 10 minutes, then 429;
  - `Panel:SessionHours` default 12, clamped to 1–168;
  - public status cache 15 s; overview poll 10 s; public page poll 30 s;
  - backup name `^rimlink-\d{8}-\d{6}\.tar\.zst$`; `/players` `limit` 1–200; `/chat` `limit` 1–500.
- CSP, verbatim, on every static response: `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'`, plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. `/api/admin/*` and `/api/panel/*` answer with `Cache-Control: no-store`.
- Front end:
  - text reaches the DOM only through `shared/dom.js` (`textContent`);
  - no `innerHTML`/`outerHTML`/`insertAdjacentHTML`/`document.write`/`eval`/`new Function`;
  - no inline `<script>`, `style=` or `on*=` attributes;
  - every visible string is a key present in both `wwwroot/i18n/en.json` and `ru.json`.
- Server conventions (AGENTS.md): options in `Hosting/`, services in `Application/<Area>`, mutations with role check and audit only in `AdminOperations`; a schema change gets a migration and a `SchemaTests` case.
- Every commit: `dotnet test Rimlink.sln -nologo -v q` green. Identity `Asofwar <7397608+Asofwar@users.noreply.github.com>` (check `git var GIT_AUTHOR_IDENT`), no trailers. Use the commit subject given in each task. Branch `asofwar/gracious-jepsen-5f80d9`; no merge, no push.
- Manual browser checks use a stand of this branch only:
  - server port `25590`, DataDir `D:\Build\rimlink-panel\server-data`;
  - the data is a copy of `D:\Build\rimlink-bt\server`, made once, source untouched; if the source is absent, start empty and register accounts through the game;
  - start: `Rimlink__Server__Port=25590 Rimlink__Server__DataDir=D:/Build/rimlink-panel/server-data Rimlink__Auth__AllowPassword=true Rimlink__Admin__ApiToken=paneltest Rimlink__Panel__ServerName="Panel test" dotnet run --project src/Rimlink.Server -c Release` (in the background);
  - open `https://localhost:25590/` and `/admin/` in the built-in browser. If the pane refuses the self-signed certificate, ask the user to accept it once or to trust a dev certificate themselves (`dotnet dev-certs https --trust` changes their trust store, so never run it yourself), then point `Rimlink__Tls__CertificatePath`/`KeyPath` at `dotnet dev-certs https --export-path D:\Build\rimlink-panel\tls\dev.pem --format Pem --no-password`;
  - never touch the servers on :25560 (checklist stand) and :25570 (BridgeTools stand) or their data; stop only the server you started (kill it by its command line containing this worktree's path).

## Review Focus

1. Player-controlled text with markup, URLs or huge length (chat line `<img src=x onerror=alert(1)>`, modpack `url` `javascript:alert(1)`, a 500-char chat line): shown literally, no script runs, the layout does not break, and only `https:` URLs become links. Pinned by `FrontendGuardTests` (Task 8) and checklist steps in Task 9 and Task 11.
2. The server restarts or the session expires while a screen is open: the next request gets 401 and the panel shows the login screen once. No error loop, no stale "logged in" header. Pinned by `api.js` 401 handling (Task 8) and checklist step in Task 13.
3. A same-origin request through a reverse proxy or on a non-default port carries `Origin` with the port: it must pass when it matches the request's scheme, host and port. Test `CookiePostWithTheSameOriginPasses` in Task 3.
4. A token pasted with surrounding spaces or a trailing newline logs in, like the Bearer path (which trims). Test `LoginTrimsTheToken` in Task 3.
5. A scheduled or manual backup prunes a file that is being downloaded (Windows keeps it locked): the prune skips that file with a warning, and the backup itself succeeds. Test `PruneSkipsAFileItCannotDelete` in Task 5.

---

### Task 1: Version, server certificate and panel options

**Files:**
- Modify: `Directory.Build.props` (add `<Version>0.4.0</Version>`)
- Create: `src/Rimlink.Server/Hosting/ServerCertificate.cs`, `src/Rimlink.Server/Hosting/ServerVersion.cs`, `src/Rimlink.Server/Hosting/PanelOptions.cs`
- Modify: `src/Rimlink.Server/Hosting/KestrelSetup.cs`, `src/Rimlink.Server/Program.cs`, `deploy/appsettings.example.json`
- Create: `tests/Rimlink.Server.Tests/Support/RepoPaths.cs`, `tests/Rimlink.Server.Tests/Support/ManualTimeProvider.cs`
- Modify: `tests/Rimlink.Server.Tests/Support/ServerFixture.cs` (add `WithServices`)
- Test: `tests/Rimlink.Server.Tests/Hosting/ServerCertificateTests.cs`, `tests/Rimlink.Server.Tests/Hosting/PanelOptionsTests.cs`

**Interfaces:**
- Produces:
  - `ServerCertificate`:
    - `public sealed class ServerCertificate(ServerOptions server, TlsOptions tls)`;
    - `public X509Certificate2 Certificate` (loaded lazily on first use: the PEM-or-self-signed logic moved out of `KestrelSetup.LoadCertificate`);
    - `public string Fingerprint` = `Convert.ToHexString(SHA256.HashData(Certificate.RawData))`;
    - `public bool SelfSigned` = `string.IsNullOrWhiteSpace(tls.CertificatePath)`.
  - `KestrelSetup.Configure(KestrelServerOptions kestrel, ServerOptions server, TlsOptions tls)` keeps its signature (used by `KestrelTlsTests`) and calls `listen.UseHttps(new ServerCertificate(server, tls).Certificate)`.
  - The DI singleton is registered from the bound options, not from the values `Program.cs` reads before `Build()`: test settings from `WebApplicationFactory` may not be visible there. Register it as `AddSingleton(sp => new ServerCertificate(sp.GetRequiredService<IOptions<ServerOptions>>().Value, sp.GetRequiredService<IOptions<TlsOptions>>().Value))`. Both instances read the same PEM file, or the same `server.pfx` (`SelfSignedCertificate.LoadOrCreate` creates it once), so they agree on the fingerprint.
  - `public static class ServerVersion { public static string Current { get; } }` — `AssemblyInformationalVersionAttribute` of the server assembly with any `+<sha>` suffix cut.
  - `PanelOptions`:
    - `public sealed class PanelOptions`, `Section = "Panel"`;
    - properties `Enabled=true`, `Public=true`, `PublicPlayerNames=true`, `SessionHours=12`, `ServerName=""`, `Description=""`, `PublicAddress=""`;
    - `public string DisplayName` (trimmed `ServerName`, or `"Rimlink"` when empty);
    - `public TimeSpan SessionLength` (`TimeSpan.FromHours(Math.Clamp(SessionHours, 1, 168))`);
    - `public string DescriptionText` (trimmed, at most 1000 chars).
  - DI registrations:
    - `Configure<PanelOptions>`;
    - the `ServerCertificate` factory above;
    - `TimeProvider.System` as singleton `TimeProvider`.
  - Test support:
    - `RepoPaths.Root`: walks up from `AppContext.BaseDirectory` to the folder holding `Rimlink.sln`;
    - `RepoPaths.WwwRoot` = `<Root>/src/Rimlink.Server/wwwroot`;
    - `ManualTimeProvider : TimeProvider` with `DateTimeOffset Now` (starts at `2026-10-29T12:00:00Z`), `GetUtcNow() => Now`, `void Advance(TimeSpan by)`;
    - `ServerFixture.WithServices(Action<IServiceCollection> configure)`, applied inside `ConfigureTestServices` after the built-in replacements.

- [ ] **Step 1: Write the failing tests**
  - `ServerCertificateTests.FingerprintMatchesTheClientPinFormat` — `fixture.Services.GetRequiredService<ServerCertificate>()`: `Fingerprint` matches `^[0-9A-F]{64}$` and equals `Convert.ToHexString(SHA256.HashData(cert.Certificate.RawData))`; `SelfSigned` is true; the file `<DataDir>/tls/server.pfx` exists afterwards.
  - `PanelOptionsTests.DefaultsMatchTheSpec` — every default above; `DisplayName == "Rimlink"`; `SessionLength == 12h`.
  - `PanelOptionsTests.SessionLengthIsClamped` — `SessionHours = 0` → `1h`; `SessionHours = 1000` → `168h`.
  - `PanelOptionsTests.ServerVersionIsTheModVersion` — `ServerVersion.Current` equals the `<modVersion>` text of `RepoPaths.Root/mod/About/About.xml`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~ServerCertificateTests|FullyQualifiedName~PanelOptionsTests" -nologo -v q` — Expected: build errors (types missing).
- [ ] **Step 3: Implement** the types and registrations above. Add the `Panel` section with all seven keys to `deploy/appsettings.example.json`.
- [ ] **Step 4: Run** the full suite `dotnet test Rimlink.sln -nologo -v q` — Expected: all green (`KestrelTlsTests` included).
- [ ] **Step 5: Commit** — `git add -A` (only this task's files), then `git commit -m "Add panel options, a shared server certificate and the server version"`.

### Task 2: Actor from the request and actor names in the audit log

**Files:**
- Create: `src/Rimlink.Server/Rest/AdminAuth.cs`, `src/Rimlink.Server/Rest/AdminToken.cs`
- Modify: `src/Rimlink.Server/Rest/AdminEndpoints.cs` (remove `RequireToken`; every route takes `HttpContext ctx` and passes `ctx.Actor()`; `/actions` adds `actorName`), `src/Rimlink.Server/Application/Admin/Actor.cs`, `src/Rimlink.Server/Application/Admin/AdminOperations.cs` (`Audit` writes `ActorName`), `src/Rimlink.Server/Storage/Entities/Entities.cs` (`AdminAction.ActorName string?`)
- Create: migration `dotnet ef migrations add AdminActionActorName --project src/Rimlink.Server`
- Test: `tests/Rimlink.Server.Tests/Admin/AuditActorTests.cs`, `tests/Rimlink.Server.Tests/Storage/SchemaTests.cs` (new case)

**Interfaces:**
- Produces:
  - `Actor.Panel` = `new(0, 0, Role.Admin, "panel")`; `public static Actor ApiAs(string? label)` → `Api` when the label is null after sanitising, else `Api with { Name = "api:" + label }`.
  - `AdminToken`: `public static bool Matches(string? expected, string? given)` — false when `expected` is blank; `given` is trimmed; constant-time comparison of the UTF-8 bytes (the logic of the old filter).
  - `AdminAuth`:
    - `public const string ActorHeader = "X-Rimlink-Actor"`;
    - `public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder group)` adds the endpoint filter;
    - `public static Actor Actor(this HttpContext context)` reads `context.Items[typeof(Actor)]`;
    - `internal static string? SanitizeLabel(string? raw)` drops `char.IsControl` characters, trims, cuts to 64, and returns null when empty.
  - The filter: Bearer via `AdminToken.Matches`, else 401. It stores `Actor.ApiAs(header)`, sets `Cache-Control: no-store`, and maps `RpcException` as before plus `AlreadyExists` → 409, `ResourceExhausted` → 429, `Unavailable` → 503.
  - `/modpack` PUT and `/modpack/defs` pass `ctx.Actor().Role` and `ctx.Actor().Name` instead of `Role.Admin, "api"`.

- [ ] **Step 1: Write the failing tests**
  - `AuditActorTests.BearerActionsAreSignedApi` — POST `/api/admin/broadcast` with Bearer; first item of GET `/api/admin/actions` has `actorName == "api"`; the response carries `Cache-Control: no-store`.
  - `AuditActorTests.ActorHeaderSignsTheAction` — the same with `X-Rimlink-Actor: discord:Mira` → `actorName == "api:discord:Mira"`.
  - `AuditActorTests.SanitizeLabelDropsControlCharactersAndCutsTo64` — `SanitizeLabel("a\tb\u0007c") == "abc"`; `SanitizeLabel(new string('x', 100)).Length == 64`; `SanitizeLabel("   ") == null`; `SanitizeLabel(null) == null`.
  - `AuditActorTests.TokenMatchingTrimsAndRefusesBlank` — `Matches("t", " t\n")` true; `Matches("", "")` false; `Matches("t", "u")` false.
  - `SchemaTests.AdminActionsHaveActorName` — after `Migrate()`, `PRAGMA table_info(admin_actions)` lists `ActorName` (same style as the existing cases).
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~AuditActorTests|FullyQualifiedName~SchemaTests" -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** the types and route changes, then add the migration (no `-nologo` on `dotnet ef`). `AdminEndpoints.MapAdminApi` uses `app.MapGroup("/api/admin").RequireAdmin()`.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green, `AdminRestTests` unchanged and passing.
- [ ] **Step 5: Commit** — `git commit -m "Take the admin actor from the request and record its name in the audit log"`.

### Task 3: Panel login, cookie sessions and CSRF checks

**Files:**
- Create: `src/Rimlink.Server/Application/Admin/PanelSessions.cs`, `src/Rimlink.Server/Rest/PanelEndpoints.cs`
- Modify: `src/Rimlink.Server/Rest/AdminAuth.cs` (cookie path, CSRF), `src/Rimlink.Server/Program.cs` (`AddSingleton<PanelSessions>()`, `app.MapPanelApi()`)
- Create: `tests/Rimlink.Server.Tests/Support/PanelTestExtensions.cs`
- Test: `tests/Rimlink.Server.Tests/Panel/PanelAuthTests.cs`

**Interfaces:**
- Consumes: `AdminToken.Matches`, `PanelOptions.SessionLength`, `Actor.Panel`, `TimeProvider`, `ClientAddress.Normalize(HttpContext.Connection.RemoteIpAddress)`.
- Produces:
  - `PanelSessions`:
    - `public sealed record PanelSession(string Id, DateTimeOffset ExpiresAt)`;
    - `public sealed class PanelSessions(IOptions<PanelOptions> options, TimeProvider time)`;
    - constants `MaxSessions = 20`, `MaxFailures = 5`, `static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10)`;
    - `PanelSession Create()` — id is 32 bytes from `RandomNumberGenerator`, base64url; evicts the oldest session past 20;
    - `PanelSession? Find(string? id)` — null when unknown or expired; expired sessions are removed;
    - `void End(string id)`;
    - `bool IsLockedOut(string ip, out TimeSpan retryAfter)`, `void RecordFailure(string ip)`.
    - The failure window starts at the first failure of the window.
    - Thread-safe (`ConcurrentDictionary` or a lock).
  - `PanelEndpoints`:
    - constants `CookieName = "rimlink_panel"`, `PanelHeader = "X-Rimlink-Panel"`;
    - `public sealed record LoginBody(string Token)`;
    - `public static void MapPanelApi(this WebApplication app)` maps the routes:
      - `POST /api/panel/login` → 200 `{expiresAt}` plus the cookie (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, `MaxAge` = session length); 401 wrong token (records the failure and logs a warning with the IP); 429 with `Retry-After` (seconds) while locked out, checked before the token;
      - `POST /api/panel/logout` → 200, ends the session and deletes the cookie;
      - `GET /api/panel/session` → 200 `{expiresAt}` or 401.
    - All three routes: 404 when `!Panel:Enabled` or `Admin:ApiToken` is blank; 403 when the CSRF check fails (login and logout); `Cache-Control: no-store`.
  - `AdminAuth`: `internal static bool PassesCsrf(HttpRequest request)` — `X-Rimlink-Panel == "1"`, and `Origin`, when present, equals `$"{request.Scheme}://{request.Host}"` (ordinal, ignore case). The filter tries Bearer first. Without an `Authorization` header it reads the cookie: an unknown session → 401; a non-GET/HEAD request failing `PassesCsrf` → 403; otherwise `Actor.Panel`.
  - Test support:
    - `Task<string> PanelLogin(this ServerFixture f, string token)` — returns the cookie value, asserts 200;
    - `HttpClient PanelClient(this ServerFixture f, string cookieValue)` — default headers `Cookie: rimlink_panel=<v>` and `X-Rimlink-Panel: 1`. TestServer is plain HTTP, so the cookie is sent by header, not by a cookie container.

- [ ] **Step 1: Write the failing tests** (fixture: `Auth:AllowPassword=true`, `Admin:ApiToken=test-admin-token`, `WithServices(s => s.AddSingleton<TimeProvider>(clock))` with a `ManualTimeProvider clock`)
  - `LoginSetsAStrictHttpOnlySecureCookie` — `Set-Cookie` contains `rimlink_panel=`, `httponly`, `secure`, `samesite=strict`, `path=/` (case-insensitive).
  - `CookieSessionCanCallTheAdminApi` — GET `/api/admin/players/online` → 200 with `Cache-Control: no-store`.
  - `LoginTrimsTheToken` — token `" test-admin-token\n"` → 200.
  - `WrongTokenIs401AndTheSixthAttemptIs429` — five wrong → 401 each; the sixth with the right token → 429 with a `Retry-After` header.
  - `LockoutEndsAfterTheWindow` — after the lockout, `clock.Advance(10 min + 1 s)` → right token → 200.
  - `LogoutEndsTheSession` — logout → 200; then GET `/api/admin/players/online` and GET `/api/panel/session` with the old cookie → 401.
  - `SessionExpiresAfterSessionHours` — `clock.Advance(12 h + 1 s)` → GET `/api/panel/session` → 401.
  - `TwentyFirstLoginEvictsTheOldest` — 21 logins; the first cookie → 401, the last → 200.
  - `CookiePostWithoutThePanelHeaderIs403` — POST `/api/admin/broadcast` with the cookie only → 403; `/actions` (Bearer) has no `broadcast`.
  - `CookiePostFromAForeignOriginIs403` — header set, `Origin: https://evil.example` → 403.
  - `CookiePostWithTheSameOriginPasses` — `Origin: http://localhost` → 200.
  - `LoginWithoutThePanelHeaderIs403`.
  - `BearerNeedsNoPanelHeader` — Bearer POST `/api/admin/broadcast` with `Origin: https://evil.example` and no panel header → 200.
  - `CookieActionsAreSignedPanel` — cookie broadcast → `actorName == "panel"`.
  - `PanelApiIsOffWithoutToken` — fixture with `Admin:ApiToken=""`: login and session → 404.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~PanelAuthTests -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** `PanelSessions`, `PanelEndpoints` and the cookie branch of the filter as specified.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** — `git commit -m "Let the panel log in with the API token and keep a cookie session"`.

### Task 4: Player directory, player card, chat history and the small admin routes

**Files:**
- Create: `src/Rimlink.Server/Application/Admin/AdminDirectory.cs`, `src/Rimlink.Server/Rest/AdminDirectoryEndpoints.cs`
- Modify:
  - `src/Rimlink.Server/Application/Admin/AdminOperations.cs`: `ListActions(Actor, int limit = 100, long? before = null)`;
  - `src/Rimlink.Server/Rest/AdminEndpoints.cs`: `/actions` adds `targetName` and `?before=`, `/bans` adds `playerName`;
  - `src/Rimlink.Server/Program.cs`: `AddScoped<AdminDirectory>()`, `app.MapAdminDirectoryApi()`.
- Test: `tests/Rimlink.Server.Tests/Admin/AdminDirectoryTests.cs`

**Interfaces:**
- Consumes: `AdminAuth.RequireAdmin`, `HttpContext.Actor()`, `AdminOperations.SetRole/RenamePlayer/SaveAll/WalletBalance`, `BanService.ListActive()`, `SessionRegistry.All/ByPlayer`, `WalletService.Balance`, `SaveStore`/`db.SaveVersions` (current = `IsCurrent`).
- Produces (`namespace Rimlink.Server.Application.Admin`):
  - Records:
    - `public sealed record PlayerRow(long Id, long AccountId, string Name, string FactionName, string Role, long? SteamId, string? Login, bool Online, DateTime? LastSeenAt, DateTime CreatedAt, DateTime? MutedUntil, bool Peaceful, bool Banned)` — `Role` lowercase (`player|moderator|admin`);
    - `public sealed record PlayerPage(int Total, List<PlayerRow> Items)`;
    - `public sealed record SettlementRow(long Id, int Tile, string Name)`;
    - `public sealed record SaveRow(int Version, long Size, long GameTick, DateTime CreatedAt)`;
    - `public sealed record BanRow(long Id, string Reason, DateTime? ExpiresAt)`;
    - `public sealed record SessionRow(string Ip, DateTime Since)`;
    - `public sealed record PlayerCard(PlayerRow Player, string? LastIp, SessionRow? Session, List<SettlementRow> Settlements, long WalletBalance, long WealthTotal, DateTime? FirstSaveAt, SaveRow? CurrentSave, List<BanRow> ActiveBans, bool VisitsClosed, DateTime? PeacefulOffAt)`. The REST response flattens `Player` into the top level: `new { …row fields…, lastIp, session, … }`.
    - `public sealed record ChatLine(long Id, DateTime At, long? FromPlayerId, string? FromName, string Kind, string Text)` — `Kind` is `player` or `system`.
  - `public sealed class AdminDirectory(RimlinkDbContext db, SessionRegistry sessions, BanService bans, WalletService wallet)` with methods:
    - `Task<PlayerPage> Search(string? q, int limit, int offset)`. `limit` outside 1–200 or `offset < 0` → `RpcException(InvalidArgument)`. Filter: `#<n>` → `Id == n`; `^\d{17}$` → `Account.SteamId == n`; otherwise name or faction contains q (case-insensitive) or `Account.Login == q`. Sort and page in memory, because online state lives in `SessionRegistry`: online first, then `LastSeenAt` descending, then `Id`. `Banned` = an active ban on the account id or the SteamId.
    - `Task<PlayerCard> Card(long playerId)` → `NotFound` when missing.
    - `Task<List<ChatLine>> Chat(long? playerId, long? before, int limit)` — only `ToPlayerId == null`; newest first; `limit` outside 1–500 → `InvalidArgument`.
  - Routes in `MapAdminDirectoryApi(this WebApplication app)` on `app.MapGroup("/api/admin").RequireAdmin()`:
    - `GET /players`, `GET /players/{id:long}`;
    - `POST /players/{id:long}/role` with body `record RoleBody(string Role)`: parsed case-insensitively into `Role`, anything else → 400 problem;
    - `POST /players/{id:long}/rename` with body `record RenameBody(string Name, string? FactionName)`;
    - `GET /economy/wallet/{id:long}` → `{balance}`;
    - `POST /saveall`;
    - `GET /chat`.

- [ ] **Step 1: Write the failing tests** (players via `RegisterAndLogin("nova", playerName: "Nova")`, `"mira"`/`"Mira"`, `"orin"`/`"Orin"`; Bearer client as in `AdminRestTests`)
  - `SearchFindsByNameFactionSteamIdLoginAndId` — `q=nov` → only Nova; `q=mira` (login) → Mira; `q=#<orin id>` → Orin. Set Orin's `Account.SteamId = 76561198000000001` in the DB, then `q=76561198000000001` → Orin.
  - `SearchPutsOnlinePlayersFirstAndPages` — kick Mira and Orin via REST; `items[0].name == "Nova"`; `limit=1&offset=1` → `total == 3`, one item.
  - `SearchRejectsOutOfRangeLimits` — `limit=0`, `limit=201`, `offset=-1` → 400 each.
  - `CardShowsSessionWalletAndSave` — POST `economy/wallet/<nova>` `{delta: 50}` → card `walletBalance == 50`, `session` not null, `online` true, `currentSave` null; GET `economy/wallet/<nova>` → `{balance: 50}`.
  - `CardShowsActiveBans` — ban Mira for 3 days → her card `banned == true`, `activeBans.Length == 1`.
  - `UnknownPlayerIs404` — `/players/999999` → 404.
  - `ChatShowsOnlyThePublicChannel` — Nova sends public `"hello"` and a direct `"secret"` to Mira (`Chat.SendAsync` with Nova's bearer metadata). `/chat` texts contain `"hello"`, not `"secret"`; `/chat?playerId=<mira>` has no line from Nova; `limit=501` → 400.
  - `RoleAndRenameWork` — role `"moderator"` → card `role == "moderator"`; role `"king"` → 400; rename Nova to `"Mira"` → 409; to an invalid name `""` → 400; to `"Nova2"` → card `name == "Nova2"`.
  - `SaveAllIsLogged` — POST `/saveall` → 200; `/actions` contains `saveall`.
  - `ActionsCarryTargetNameAndPageBackwards` — kick Nova → `actions[0].targetName == "Nova"`; `?before=<actions[0].id>` has no item with that id.
  - `BansCarryThePlayerName` — ban Mira → `/bans[0].playerName == "Mira"`; an IP-only ban has `playerName == null`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~AdminDirectoryTests -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** `AdminDirectory`, the routes, `ListActions` paging, and the added `targetName`/`playerName` (one lookup of players by the result's account ids).
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** — `git commit -m "Add the player directory, player card, chat history and role, rename and save-all routes"`.

### Task 5: Backups over REST

**Files:**
- Create: `src/Rimlink.Server/Backup/BackupService.cs`
- Modify:
  - `src/Rimlink.Server/Backup/BackupScheduler.cs`: create through `BackupService`; `Prune` skips files it cannot delete;
  - `src/Rimlink.Server/Application/Admin/AdminOperations.cs`: `CreateBackup`, `OpenBackup`;
  - `src/Rimlink.Server/Rest/AdminDirectoryEndpoints.cs`;
  - `src/Rimlink.Server/Program.cs`: `AddSingleton<BackupService>()`.
- Test: `tests/Rimlink.Server.Tests/Backup/BackupRestTests.cs`

**Interfaces:**
- Consumes: `BackupArchive.Create(string dataDir, string outFile)`, `DataDirectory.Backups/Root`, `BackupOptions.Keep`, `TimeProvider`.
- Produces:
  - `public sealed record BackupFile(string Name, long Size, DateTime CreatedAt)`.
  - `public sealed class BackupService(DataDirectory data, IOptions<BackupOptions> options, TimeProvider time, ILogger<BackupService> log)`:
    - `public const string NamePattern = @"^rimlink-\d{8}-\d{6}\.tar\.zst$"`;
    - `List<BackupFile> List()` — newest first;
    - `Task<BackupFile> CreateNow()` — fails fast with `RpcException(AlreadyExists, "a backup is already running")` when the gate is taken, and with `AlreadyExists` when the file name exists; the name comes from `time.GetUtcNow()`; `Task.Run(BackupArchive.Create)`, then `BackupScheduler.Prune(data.Backups, options.Value.Keep, log)`;
    - `Task<BackupFile> CreateScheduled()` — waits for the gate instead of failing;
    - `FileInfo? Open(string name)` — null unless the name matches the pattern and the file exists in `data.Backups`;
    - `internal IDisposable? TryAcquireGate()` — the one `SemaphoreSlim(1,1)`, used by both create methods and by the test.
  - `BackupScheduler.Prune(string backupsDir, int keep, ILogger? log = null)` — same signature for existing callers plus the optional logger. An `IOException` or `UnauthorizedAccessException` on one file → warning, continue.
  - `AdminOperations`:
    - `Task<BackupFile> CreateBackup(Actor actor)` — `Require(Admin)`, audit `backup` with the name;
    - `Task<FileInfo?> OpenBackup(Actor actor, string name)` — `Require(Admin)`, audit `backup-download` only when the file exists.
  - Routes:
    - `GET /api/admin/backups`;
    - `POST /api/admin/backups` → `{name, size}`;
    - `GET /api/admin/backups/{name}` → `Results.File(path, "application/octet-stream", name)`, or 404.

- [ ] **Step 1: Write the failing tests**
  - `CreateListAndDownload` — POST → 200, `name` matches the pattern; GET list contains it; downloaded bytes equal `File.ReadAllBytes(<DataDir>/backups/<name>)`; `/actions` contains `backup` and `backup-download`.
  - `BadNamesAre404` — `GET backups/rimlink.db`, `GET backups/..%2Frimlink.db`, `GET backups/rimlink-20261029-120000.tar.zst` (absent) → 404 each.
  - `SecondBackupInTheSameSecondIs409` — `ManualTimeProvider` fixed; two POSTs → 200 then 409.
  - `BackupWhileOneRunsIs409` — `using var gate = service.TryAcquireGate()` (not null); POST → 409.
  - `PruneSkipsAFileItCannotDelete` — three files in a temp dir; the oldest opened with `FileShare.None`; `BackupScheduler.Prune(dir, keep: 1)` does not throw; the newest still exists.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~BackupRestTests|FullyQualifiedName~BackupArchiveTests" -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** as specified.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** — `git commit -m "Create, list and download backups over the admin API"`.

### Task 6: Server status, config view and the public status

**Files:**
- Create: `src/Rimlink.Server/Application/Admin/ServerStatus.cs`, `src/Rimlink.Server/Application/Admin/PublicStatusCache.cs`, `src/Rimlink.Server/Rest/PublicEndpoints.cs`
- Modify: `src/Rimlink.Server/Rest/AdminDirectoryEndpoints.cs` (`/status`, `/config`), `src/Rimlink.Server/Program.cs` (`AddScoped<ServerStatus>()`, `AddSingleton<PublicStatusCache>()`, `app.MapPublicApi()`)
- Test: `tests/Rimlink.Server.Tests/Admin/StatusConfigTests.cs`, `tests/Rimlink.Server.Tests/Panel/PublicStatusTests.cs`

**Interfaces:**
- Consumes:
  - `AdminOperations.EconomyStats(Actor)` and `RaidStats(Actor)` (called with `Actor.Panel`);
  - `LiveSessionService.FinalStates` (live active = not in it);
  - `SessionRegistry.All`, `ModpackService.Get()`, `ServerCertificate`, `ServerVersion.Current`;
  - `BackupService.List()`, all `IOptions<*Options>`, `TimeProvider`.
- Produces:
  - `public sealed class ServerStatus(...)` (scoped), with methods:
    - `Task<object> Admin()` — fields of spec §4 `/status`: `version`, `protocolVersion`, `startedAt`, `uptimeSeconds`, `playersOnline`, `maxPlayers`, `playersTotal`, `worldExists`, `worldCreatedAt`, `settlements`, `economy {frozen, reason}`, `raids {frozen, reason, active}`, `liveActive`, `saves {count, totalBytes}`, `backups {count, lastAt}`, `certificateFingerprint`, `certificateSelfSigned`. A failure in one part → that field `null`, plus a warning in the log. `startedAt` = `Process.GetCurrentProcess().StartTime.ToUniversalTime()`, captured once.
    - `JsonObject Config()` — sections `server`, `tls`, `auth`, `backup`, `economy`, `raids`, `live`, `panel` (options serialised with web defaults) plus `admin: {apiToken}`. Secrets come from `internal static readonly (string Section, string Key)[] Secrets = [("server","serverPassword"), ("auth","steamWebApiKey"), ("admin","apiToken")]` and become `"set"` when non-blank, otherwise `""`.
    - `Task<PublicStatus> Public()` — record with the spec §4 fields except `address`.
  - `public sealed class PublicStatusCache(TimeProvider time)` (singleton): `Task<PublicStatus> Get(Func<Task<PublicStatus>> build)`. Rebuilds when the cached value is older than 15 s; two concurrent misses build once (`SemaphoreSlim`).
  - `PublicEndpoints.MapPublicApi(this WebApplication app)`: `GET /api/public/status`, 404 unless `Panel:Enabled && Panel:Public`. The response is the cached `PublicStatus` plus `address` = `Panel:PublicAddress`, or `$"{Request.Host.Host}:{Server:Port}"` when blank.
  - Field rules: `players` is null when `!PublicPlayerNames`; `serverName = DisplayName`; `description = DescriptionText`; `passwordRequired = !string.IsNullOrEmpty(Server:ServerPassword)`; `steamLogin = !string.IsNullOrEmpty(Auth:SteamWebApiKey)`; `passwordLogin = Auth:AllowPassword`.

- [ ] **Step 1: Write the failing tests**
  - `StatusConfigTests.StatusReportsPlayersWorldAndCertificate` — Nova online → `playersOnline == 1`, `playersTotal == 1`, `worldExists == true`, `version == ServerVersion.Current`, `certificateFingerprint == <ServerCertificate>.Fingerprint`, `certificateSelfSigned == true`, `saves.count == 0`.
  - `StatusConfigTests.ConfigHidesSecrets` — settings `Server:ServerPassword=pw-secret-1`, `Auth:SteamWebApiKey=steam-secret-2`, token `test-admin-token`. Raw bodies of `/config` and `/status` contain none of the three strings. `config.server.serverPassword == "set"`, `config.auth.steamWebApiKey == "set"`, `config.admin.apiToken == "set"`, `config.economy.marketFeePercent == 0`, `config.panel.sessionHours == 12`.
  - `PublicStatusTests.PublicStatusNeedsNoAuth` — anonymous GET → 200, `serverName == "Rimlink"`.
  - `PublicStatusTests.PublicStatusHasNoAddressesOrSteamIds` — `Panel:PublicAddress=play.example:25560`; Nova online with SteamId `76561198000000001` set in the DB. The body contains neither `127.0.0.1` nor `76561198000000001`; `address == "play.example:25560"`; `players[0].name == "Nova"`.
  - `PublicStatusTests.PlayerNamesCanBeHidden` — `Panel:PublicPlayerNames=false` → `players` is JSON null, `playersOnline == 1`.
  - `PublicStatusTests.PublicStatusCanBeTurnedOff` — `Panel:Public=false` → 404.
  - `PublicStatusTests.ModpackAndLoginModesAreShown` — PUT modpack (one entry `Ludeon.RimWorld`, `workshopId 0`) and `Server:ServerPassword=x` → `modpack[0].packageId == "ludeon.rimworld"`, `passwordRequired`, `passwordLogin` true, `steamLogin` false.
  - `PublicStatusTests.PublicStatusIsCachedFor15Seconds` — `ManualTimeProvider`. First GET `playersOnline == 0`; log Nova in; second GET still 0; `Advance(16 s)` → 1.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~StatusConfigTests|FullyQualifiedName~PublicStatusTests" -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** as specified.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** — `git commit -m "Add the server status, the redacted config view and the public status"`.

### Task 7: CLI commands for the new routes

**Files:**
- Modify: `src/Rimlink.Tools/Commands/AdminCommand.cs`, `src/Rimlink.Tools/Commands/RestClient.cs`, `tests/Rimlink.Server.Tests/Rimlink.Server.Tests.csproj` (add `<ProjectReference Include="..\..\src\Rimlink.Tools\Rimlink.Tools.csproj" />`)
- Test: `tests/Rimlink.Server.Tests/Tools/AdminCommandTests.cs`

**Interfaces:**
- Produces:
  - `RestClient`: `public RestClient(HttpClient http)` (the caller sets `BaseAddress` ending in `/api/admin/` and the auth header; the existing constructor delegates to it); `public async Task Download(string path, string file)` (non-2xx → the same `InvalidOperationException` as `Read`).
  - `AdminCommand`: `public static Task<string> Run(RestClient rest, string command, string[] a)` — the whole current `switch` moves here unchanged; `SetAction` calls it. New cases:
    - `players [query…]` → `players?q=<escaped joined query>`;
    - `player <id>`;
    - `role <id> <role>`;
    - `rename <id> <name> [faction…]`;
    - `saveall`, `chat [playerId]`, `status`, `config`, `backups`;
    - `backup` → POST `backups`;
    - `backup-get <name> <file>` → `Download`, returns `saved <file>`.
    - The `command` argument description lists the new commands.
- If the compiler reports an ambiguous `Program` after adding the Tools reference, alias the Tools reference (`<Aliases>tools</Aliases>`) rather than renaming anything in either project.

- [ ] **Step 1: Write the failing tests** — `RestClient` over `fixture.CreateClient()` with `BaseAddress = new Uri(client.BaseAddress!, "/api/admin/")` and a Bearer header.
  - `PlayersAndPlayerPrintJson` — `Run(rest, "players", ["nov"])` contains `"Nova"`; `Run(rest, "player", [novaId])` contains `walletBalance`.
  - `RoleRenameSaveAllStatusAndConfigRun` — each returns without throwing; `role` → card shows `moderator`; `config` output does not contain the token.
  - `BackupAndBackupGet` — `Run(rest, "backup", [])` JSON has a `name`; `Run(rest, "backup-get", [name, tmp])` returns `saved <tmp>` and the bytes equal the server file.
  - `UnknownCommandThrows` — `Run(rest, "nope", [])` throws `InvalidOperationException`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~AdminCommandTests -nologo -v q` — Expected: build errors / FAIL.
- [ ] **Step 3: Implement** as specified.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Commit** — `git commit -m "Mirror the new admin routes in rimlink-tools"`.

### Task 8: Static hosting, security headers and the front-end shell

**Files:**
- Create: `src/Rimlink.Server/Rest/PanelHosting.cs`
- Modify: `src/Rimlink.Server/Program.cs` (`app.UsePanelStaticFiles()` right after `app.UseHttpMetrics()`)
- Create in `src/Rimlink.Server/wwwroot/`:
  - `index.html` and `public.js` (placeholder: title from `/api/public/status`, real page in Task 9);
  - `admin/index.html`, `admin/app.js`, `admin/api.js`, `admin/views/login.js`;
  - `shared/dom.js`, `shared/i18n.js`, `shared/format.js`;
  - `i18n/en.json`, `i18n/ru.json`, `style.css`.
- Test: `tests/Rimlink.Server.Tests/Panel/StaticFilesTests.cs`, `tests/Rimlink.Server.Tests/Panel/FrontendGuardTests.cs`

**Interfaces:**
- Produces (server): `PanelHosting.UsePanelStaticFiles(this WebApplication app)`.
  - A gate middleware returns 404 for paths starting with `/admin` unless `Panel:Enabled` and `Admin:ApiToken` is non-blank.
  - For `/`, `/index.html`, `/public.js` it returns 404 unless `Enabled && Public`.
  - For every other static path it returns 404 unless `Enabled`.
  - Then `UseDefaultFiles()` and `UseStaticFiles(OnPrepareResponse = add CSP, nosniff, Referrer-Policy)`.
  - `PublicEndpoints` and `PanelEndpoints` already honour `Enabled`.
- Produces (front end, ES modules; later tasks rely on these names):
  - `shared/dom.js`:
    - `h(tag, attrs, ...children)`. `attrs` keys: `class`, `id`, `title`, `type`, `value`, `placeholder`, `disabled`, `checked`, `name`, `for`, `min`, `max`, `data-*`, `onClick`/`onInput`/`onChange`/`onSubmit` as functions (attached with `addEventListener`). `href` is kept only when it starts with `https://`, `#` or `/`; otherwise it is dropped. Links with `https://` get `rel="noopener noreferrer"` and `target="_blank"`. Children: strings and numbers become text nodes, `null`/`false` are skipped, nodes are appended.
    - `clear(el)`.
    - `table(columns, rows)` — `columns: [{key, label, render?(row) → Node|string}]`; wraps the table in a `div.table-wrap` that scrolls horizontally.
    - `busyButton(label, action)` — a button disabled while the returned promise is pending.
    - `banner(message, kind)` — shows a top banner, auto-hides `info` after 5 s.
    - `confirmDialog({title, text, typeToConfirm?})` → `Promise<boolean>`; with `typeToConfirm`, OK stays disabled until the input equals it.
  - `shared/i18n.js`: `loadI18n()` (fetches `/i18n/<lang>.json`), `t(key, params)` (`{name}` substitution; a missing key returns the key), `lang()`, `setLang(code)`. Language order: `localStorage['rimlink.lang']` inside try/catch, then `navigator.language` starting with `ru` → `ru`, else `en`.
  - `shared/format.js`: `fmtDate(iso)` (local date-time), `fmtBytes(n)`, `fmtDuration(seconds)`, `fmtSilver(n)`.
  - `admin/api.js`: `export class ApiError extends Error { status; detail }` and `export const api = { get(path), post(path, body), put(path, body), del(path), panel: { login(token), logout(), session() } }`. Paths are relative to `/api/admin/`. Every call sends `X-Rimlink-Panel: 1`, `credentials: 'same-origin'` and JSON. A non-2xx throws `ApiError` with problem+json `detail`. A 401 dispatches `window` event `rimlink:unauthorized` once per burst, then throws.
  - `admin/app.js`: routes `{ '': overview, 'players': players, 'players/:id': player, 'bans', 'economy', 'raids', 'live', 'chat', 'modpack', 'backups', 'server', 'actions' }`; each view module exports `render(root, params) → (() => void) | void` (the returned function stops its polling when the route changes).
    - On start: `loadI18n()`, then `api.panel.session()`; 401 → `views/login.js`.
    - Header: server name from `/status` or `/api/public/status`, online N/M, freeze flags, language switch, logout.
    - Left nav with every route. Routes without a module yet show a "coming soon" text key until their task lands.
    - `rimlink:unauthorized` → login view.
  - `admin/views/login.js`: a password field and a submit button. 401 → `login.wrong`. 429 → `login.lockedOut` with the minutes taken from `Retry-After`. On success the field is cleared and the shell renders. The token is never written to storage.
- i18n keys use dotted names: `nav.*`, `login.*`, `common.*` (save, cancel, confirm, refresh, loadMore, error.network, …), `header.*`.

- [ ] **Step 1: Write the failing tests**
  - `StaticFilesTests.PublicPageAndAdminAreServedWithCsp` — GET `/` and GET `/admin/` → 200 `text/html`; `Content-Security-Policy` equals the Global Constraints string; `X-Content-Type-Options == nosniff`; `Referrer-Policy == no-referrer`.
  - `StaticFilesTests.AdminIsHiddenWithoutToken` — `Admin:ApiToken=""` → `/admin/` 404, `/` 200.
  - `StaticFilesTests.PublicPageCanBeTurnedOff` — `Panel:Public=false` → `/` 404, `/admin/` 200, `/style.css` 200.
  - `StaticFilesTests.PanelOffHidesEverything` — `Panel:Enabled=false` → `/`, `/admin/`, `/style.css`, `/api/public/status`, `/api/panel/session` → 404.
  - `StaticFilesTests.GrpcStillWorks` — `RegisterAndLogin("nova")` succeeds on the same fixture.
  - `FrontendGuardTests.NoHtmlInjectionSinks` — every `RepoPaths.WwwRoot/**/*.js`: no match for `\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|\beval\s*\(|new\s+Function\s*\(`.
  - `FrontendGuardTests.HtmlHasNoInlineCode` — every `*.html`: no `<script>` without `src=`, no ` on[a-z]+=`, no ` style=`.
  - `FrontendGuardTests.DictionariesHaveTheSameKeys` — flattened key sets of `en.json` and `ru.json` are equal; no empty value.
  - `FrontendGuardTests.EveryUsedKeyExists` — every `\bt\(\s*'([A-Za-z0-9_.]+)'` in `*.js` is a key of `en.json`.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter "FullyQualifiedName~StaticFilesTests|FullyQualifiedName~FrontendGuardTests" -nologo -v q` — Expected: FAIL.
- [ ] **Step 3: Implement** `PanelHosting`, then the shell files. `style.css` defines tokens on `:root` with a `prefers-color-scheme: dark` override, plus layout (header, left nav that collapses above the content below 720 px), tables, forms, banner and dialog.
- [ ] **Step 4: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 5: Manual check** on the stand: `/admin/` shows the login. A wrong token shows a banner. The right token shows the shell with the header and nav. The ru/en switch relabels everything. Logout returns to the login.
- [ ] **Step 6: Commit** — `git commit -m "Serve the panel shell and public page with security headers"`.

### Task 9: Public page

**Files:**
- Modify: `src/Rimlink.Server/wwwroot/index.html`, `public.js`, `i18n/en.json`, `i18n/ru.json`, `style.css`

**Interfaces:**
- Consumes: `GET /api/public/status` (Task 6), `shared/dom.js`, `shared/i18n.js`, `shared/format.js` (Task 8).
- Behaviour (spec §5):
  - header with `serverName`, `description`, online N/M, version;
  - "How to connect": `address`, login modes (Steam / password), whether a server password is needed, `certificateFingerprint` in groups of 4 with a copy button (`navigator.clipboard.writeText`, failure → select the text) and one line saying it must match the trust window on first connect;
  - modpack table in `position` order: `packageId`, required/allowed, pinned version, Workshop link when `workshopId > 0`, `url` as a link only when it starts with `https://` (enforced by `h`), otherwise text;
  - online players (section hidden when `players === null`);
  - world stats;
  - poll every 30 s; on a failed poll keep the last data and show `public.offline`;
  - no link to `/admin`.

- [ ] **Step 1: Implement** the page.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~FrontendGuardTests -nologo -v q` — Expected: PASS.
- [ ] **Step 3: Manual check** on the stand:
  - every section renders in ru and en;
  - the fingerprint equals the server's `/api/admin/status` value;
  - a modpack entry with `url` `javascript:alert(1)` (set via `PUT /api/admin/modpack`) shows as plain text;
  - stopping the server shows the offline mark after the next poll;
  - the page is usable at 375 px width.
- [ ] **Step 4: Commit** — `git commit -m "Add the public server page"`.

### Task 10: Admin overview, players and the player card

**Files:**
- Create: `src/Rimlink.Server/wwwroot/admin/views/overview.js`, `players.js`, `player.js`
- Modify: `admin/app.js` (wire routes), `i18n/en.json`, `i18n/ru.json`, `style.css`

**Interfaces:**
- Consumes:
  - `api` and the view contract (Task 8);
  - routes `GET status`, `GET players/online`, `GET actions?limit=10`, `GET players`, `GET players/{id}`;
  - `POST players/{id}/kick|mute|role|rename|peaceful|reset`, `DELETE players/{id}/mute`, `POST bans`;
  - `GET players/{id}/saves`, `POST players/{id}/saves/{v}/rollback`;
  - `POST economy/wallet/{id}`, `GET economy/parcels/{id}`, `GET economy/lots?playerId=`, `POST economy/refund/lot|parcel/{id}`;
  - `GET shop/{id}`, `GET shop/{id}/removals`, `GET raids?playerId=`, `GET live?playerId=`, `GET chat?playerId=`.
- Behaviour (spec §5):
  - **Overview** polls every 10 s: status cards, online table (name → `#/players/{id}`, IP, since, kick and mute-30-min buttons), last 10 actions.
  - **Players:** search box (debounced 300 ms) and pages of 50 (`total`); badges online/banned/muted/peaceful.
  - **Card:** the blocks listed in spec §5.
    - Ban form: days empty = permanent; the body is `{playerId, reason, days}`.
    - Reset needs `confirmDialog({typeToConfirm: <player name>})`. Rollback, ban and refunds need `confirmDialog`.
    - Every action refreshes the card. A 409/400 `detail` shows in a banner.

- [ ] **Step 1: Implement** the three views.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~FrontendGuardTests -nologo -v q` — Expected: PASS.
- [ ] **Step 3: Manual check** on the stand with the copied data:
  - search by name, SteamID and `#id`;
  - open a card; mute then unmute;
  - change the role and back;
  - rename to a taken name → banner with the 409 detail;
  - wallet +10 then −10;
  - every block loads;
  - a double click on a button sends one request (network panel).
- [ ] **Step 4: Commit** — `git commit -m "Add the panel overview, player list and player card"`.

### Task 11: Bans, economy, raids, live sessions and chat screens

**Files:**
- Create: `src/Rimlink.Server/wwwroot/admin/views/bans.js`, `economy.js`, `raids.js`, `live.js`, `chat.js`
- Modify: `admin/app.js`, `i18n/en.json`, `i18n/ru.json`

**Interfaces:**
- Consumes:
  - `GET bans`, `POST bans` (`{playerId?|steamId?|ip?, reason, days}`), `DELETE bans/{id}`;
  - `GET economy/stats`, `POST economy/freeze {frozen, reason}`, `GET economy/lots`, `POST economy/refund/lot/{id}`;
  - `GET raids/stats`, `GET raids`, `POST raids/freeze`, `POST raids/{id}/cancel {reason, returnPawns}`;
  - `GET live`, `POST live/{id}/end {reason}`;
  - `GET chat?playerId=&before=&limit=100`, `POST players/{id}/mute`.
- Behaviour (spec §5): player ids in every table link to the card. The raid list has a state filter (client-side over the loaded list). Chat has a player filter and "load more" (`before` = smallest loaded id). Refund, cancel and end need `confirmDialog`.

- [ ] **Step 1: Implement** the five views.
- [ ] **Step 2: Run** `dotnet test tests/Rimlink.Server.Tests --filter FullyQualifiedName~FrontendGuardTests -nologo -v q` — Expected: PASS.
- [ ] **Step 3: Manual check** on the stand:
  - ban by IP, then pardon;
  - freeze and unfreeze the economy and raids (header flags follow);
  - the lists load;
  - chat paging;
  - a chat line `<img src=x onerror=alert(1)>` (sent from a game, or inserted into the copied DB with `sqlite3`) shows literally, and a 500-char line wraps.
- [ ] **Step 4: Commit** — `git commit -m "Add the panel bans, economy, raids, live sessions and chat screens"`.

### Task 12: Modpack editor, backups, server and audit log screens

**Files:**
- Create: `src/Rimlink.Server/wwwroot/admin/views/modpack.js`, `backups.js`, `server.js`, `actions.js`
- Modify: `admin/app.js`, `i18n/en.json`, `i18n/ru.json`

**Interfaces:**
- Consumes:
  - `GET modpack`, `PUT modpack {entries:[{position, packageId, version, workshopId, url, kind}]}` (`kind` `"required"|"allowed"`);
  - `GET backups`, `POST backups`, `/api/admin/backups/{name}` as an `<a href download>` (same-origin GET carries the cookie);
  - `POST broadcast {text}`, `POST saveall`, `POST shutdown {inSeconds, reason}`, `POST world/recreate`, `GET config`;
  - `GET actions?limit=100&before=`.
- Behaviour (spec §5):
  - **Modpack:** editable rows with up/down, add and delete. Positions are renumbered 0..n-1 on save. Save shows a diff (added / removed / changed by `packageId`) in `confirmDialog`, then PUT.
  - **Backups:** list and "make now" (409 → banner), plus the restore hint.
  - **Server:** shutdown needs `confirmDialog`; recreate world needs `typeToConfirm` = `serverName` from status, or `RECREATE` when the name is the default `Rimlink`. Config is read-only, one table per section.
  - **Actions:** `actorName ?? (actorAccountId == 0 ? 'api' : actorAccountId)`, action, target, details, time, plus "load more".

- [ ] **Step 1: Implement** the four views.
- [ ] **Step 2: Run** `dotnet test Rimlink.sln -nologo -v q` — Expected: all green.
- [ ] **Step 3: Manual check** on the stand:
  - edit the modpack (reorder, add, remove) and see the diff, then restore it;
  - make a backup and download it; the size matches;
  - broadcast;
  - config shows `set` for the token;
  - the audit log shows `panel` for these actions.
  - Do not shut down or recreate on the stand without restoring its data copy afterwards.
- [ ] **Step 4: Commit** — `git commit -m "Add the panel modpack editor, backups, server and audit log screens"`.

### Task 13: Documentation, checklist and the full run

**Files:**
- Modify: `docs/server-setup.md`, `docs/player-guide.md`, `docs/manual-checklist.md`, `README.md`, `deploy/docker-compose.yml`, `AGENTS.md`

**Interfaces:**
- `server-setup.md`:
  - new section "Web panel": `/admin/` login with `Admin:ApiToken`, cookie lifetime `Panel:SessionHours`, lockout after 5 failures in 10 minutes;
  - a real certificate via `Tls:CertificatePath`/`KeyPath`; the public page and its keys; `Panel:Enabled=false`;
  - that `/metrics` stays open as before;
  - ruling note: a manual backup counts towards `Backup:Keep`;
  - section 6 lists the new REST routes, the `X-Rimlink-Actor` header and the new CLI commands.
- `docker-compose.yml`: commented `Rimlink__Panel__ServerName` / `Rimlink__Panel__PublicAddress` lines.
- `player-guide.md`: the public page (modpack, fingerprint check).
- `README.md`: status line mentions the web panel.
- `AGENTS.md`: convention 5 adds `Directory.Build.props` `<Version>` to the places kept equal; the layout table notes `src/Rimlink.Server/wwwroot` (panel and public page).
- `manual-checklist.md`: new section "Веб-панель (ветка `asofwar/gracious-jepsen-5f80d9`)" with one line per manual check of Tasks 8–12, plus the run below.

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Full run on the stand:**
  - every screen once in ru and en;
  - restart the server with the admin open → the next poll lands on the login screen, once, without an error loop;
  - a game client connected to `:25590` kicked from the panel gets the kick notice. For this, ask session `local_76666afd-3a4a-4ad8-8458-66aa6bf6165d` whether the BridgeTools stand game (`rimlink-bt-host`) is free; if not, mark this line `[ ]` with the reason.
  - Record results in the checklist.
- [ ] **Step 3: Run** `dotnet test Rimlink.sln -nologo -v q` and `dotnet build Rimlink.sln -nologo -v q` — Expected: green, 0 errors.
- [ ] **Step 4: Commit** — `git commit -m "Document the web panel and record its manual run"`.
- [ ] **Step 5:** Stop the stand server you started; leave `D:\Build\rimlink-panel` in place for the review.
