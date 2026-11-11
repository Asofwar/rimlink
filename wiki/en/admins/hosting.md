---
pageId: admins/hosting
contentRevision: 15
---

# Hosting and startup

Applies to: Rimlink 0.10.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Server and CLI release 0.9.2 require Microsoft.NETCore.App 10.0.14 and Microsoft.AspNetCore.App 10.0.14. Check both with dotnet --list-runtimes. Their runtimeconfigs use RollForward=Disable, so a missing patch stops the process before startup. SDK 10.0.403 includes the required frameworks. Other patches may coexist; qualify and release a later security patch before changing this exact runtime policy.

The server is ASP.NET Core 10 with gRPC/HTTP2, REST and SQLite. Source builds require the SDK pinned in global.json; framework-dependent releases require the matching runtime. Persist all of Server:DataDir, not only the database.

The repository Compose example exposes 25560:25560 and mounts ./data to /data. Supply environment secrets before startup. Use deploy/Dockerfile for a local image build. Outside Docker, run the release executable from a stable working directory: relative paths resolve there.

A proxy must support HTTP/2/gRPC and long-lived streams, not just the panel page. Verify saves and a live stream from an external client. Shut down gracefully after notifying players.

### Minimal source startup

Install the SDK pinned in global.json and use a dedicated data directory. Run these examples from the repository root. Password login is enabled for this example; production secrets belong in your environment, never Git.

```powershell
# Windows / PowerShell
$env:Rimlink__Server__DataDir='./data'
$env:Rimlink__Auth__AllowPassword='true'
$env:Rimlink__Auth__AdminLogins__0='admin'
dotnet run --project src/Rimlink.Server -c Release
```

```sh
# Linux
Rimlink__Server__DataDir=./data Rimlink__Auth__AllowPassword=true Rimlink__Auth__AdminLogins__0=admin dotnet run --project src/Rimlink.Server -c Release
```

### Container startup

From the repository root, supply environment secrets, then run `docker compose -f deploy/docker-compose.yml up -d`. Inspect `docker compose -f deploy/docker-compose.yml logs --tail=100`. The example uses latest; pin a tested tag/digest for production. Do not run beside a bare server using the same DataDir. Stop with `docker compose -f deploy/docker-compose.yml down`; do not request volume deletion.

### Minimal configuration and acceptance

For a framework-dependent release, appsettings.json in the server directory uses top-level sections, not a Rimlink JSON wrapper:

```json
{"Server":{"Port":25560,"DataDir":"./data"},"Auth":{"AllowPassword":true,"AdminLogins":["admin"]}}
```

Run `dotnet Rimlink.Server.dll` from a stable working directory. TLS is enabled by default: verify the fingerprint through a trusted channel on first connection; configure Tls:CertificatePath and Tls:KeyPath (PEM) for a public certificate. Allow TCP 25560 through the host/router only for the intended network. Expected result: a listening server, a healthy /health response, and a client that can save and reload a colony. A working panel alone does not prove the proxy supports game HTTP/2 streams.

## Network and persistence: verify externally {#host-checklist}

Test the TCP endpoint from a player's machine, not just localhost. NAT must reach the correct host; proxies must pass gRPC HTTP/2 and long streams. HTML success proves no streaming support. Verify login, save transfer and live streaming with test colonies.

For containers, inspect the DataDir mount and write permissions. Recreating a container must not create an empty world. Never share a live DataDir with a parallel bare server. Preserve process settings, certificates and secrets separately: a world archive is not an infrastructure backup.

## Limits and safety {#limits}

Do not run multiple servers against one DataDir. Do not independently copy a live database and saves as an ad-hoc backup. TLS is required by default; plain HTTP is for isolated development only.


appsettings.example.json keeps GuildCapture disabled by default. Its separate executor identity/profile/credential parameters are documented in the configuration reference; executor registration does not start a campaign or enable Product qualification. A dedicated DataDir, common storage barrier, and separate licensed game process remain prerequisites for the future battle stand.

## See also {#related}

- [Configuration reference](./configuration.md)
- [Monitoring and event feed](./monitoring.md)
- [Backup and recovery](./backup-recovery.md)

A separate development-only `tools/Rimlink.GuildAcceptanceHost` is excluded from the release package. Commands `prepare`, `provision`, `inspect`, `run` and `admit-slice` use a separate owned stand with an empty DataDir, anchor, three savedata roots, pinned source and binaries, and separate credentials. The server listens on loopback with TLS and allows two named password logins and one executor. A separate offline `restore` command admits a verified archive from an owned SLICE stand into a new owned stand. HTTP reset and restore are refused. See [backup and recovery](backup-recovery.md#guild-domain-v3).

## Isolated capture setup clock {#guild-setup-clock}

Only the dev `Rimlink.GuildAcceptanceHost` exposes operator `GET /api/admin/acceptance/clock/` and `POST /api/admin/acceptance/clock/advance` with the `admin` scope. A step requires HOME, first saves for both allowed test identities, the exact current cut/revision and no Active battle, including cleanup phases. Purpose is EligibilitySetup or ScheduledWindowNotice. A step is at most 7 days; the immutable bootstrap budget is 30 days/32 steps. It cannot cross the earliest declared window. New registrations are closed after the first advance.

The step is a protected GuildCaptureOperation in the common chain; the domain clock publishes after commit. Exact replay never adds time twice; the verified prefix recovers before requests are admitted. This is not a separate financial ledger. Auth/TLS, leases, challenges, networking, AI and the 20-minute battle limit use real clocks. A step neither produces/issues resources nor enables SLICE/Product. Virtual days do not establish multi-day runtime stability.

In the isolated host, the background scheduler keeps waiting for qualified HOME admission while GuildCapture.Enabled=false. Preparing a due campaign requires the verified common V3 authority and its current grant; a server with `GuildCapture:Enabled` off and without that admission starts no campaign. Simulation and resource issuance remain forbidden in HOME.

The guild background scheduler waits for the SaveFiles storage barrier S when another writer temporarily withdraws the published cut. Full admission is checked under S→B; an early transient-cut check must not skip a preparation or cancellation pass. The unit regression proves writer waiting; repeated native delay verification after this fix is still pending.
