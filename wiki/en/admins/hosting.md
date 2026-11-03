---
pageId: admins/hosting
contentRevision: 2
---

# Hosting and startup

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

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

## See also {#related}

- [Configuration reference](./configuration.md)
- [Monitoring and event feed](./monitoring.md)
- [Backup and recovery](./backup-recovery.md)
