# Spike: gRPC inside RimWorld 1.7

Throwaway code. Question: does `Grpc.Net.Client` over HTTP/2 + TLS work from a mod
running inside RimWorld 1.7 (Waylith engine, `.NET 10.0.13-waylithclr`)?

## Verdict: gRPC works

Run on 2026-10-28, RimWorld 1.7.4990 rev1069, Windows 11. The mod's static
constructor called `Ping.Echo` on `https://127.0.0.1:25560` and wrote:

```
runtime: 10.0.13
OK: hi @ 2026-10-28T15:15:09.1523660Z
```

No `TypeLoadException` / `FileLoadException`. The transport decision from the spec
stands: gRPC + HTTP/2 + TLS.

## DLLs the mod must ship

Copied next to the mod assembly (`Assemblies/`):

- `Grpc.Net.Client.dll` 2.84.0
- `Grpc.Net.Common.dll` 2.84.0
- `Grpc.Core.Api.dll` 2.84.0
- `Google.Protobuf.dll` 3.36.2

Not shipped, the game already carries them:

- `Microsoft.Extensions.Logging.Abstractions.dll` (game: 10.0.25)
- `Microsoft.Extensions.DependencyInjection.Abstractions.dll`
- `System.Diagnostics.DiagnosticSource.dll`

## How it was run

1. `dotnet run --project Spike.Server` (Kestrel, HTTP/2, ASP.NET Core dev certificate).
2. `dotnet run --project Spike.Check -- hi` — plain .NET client, sanity check of the server.
3. `dotnet build Spike.Mod -c Release -p:RimWorldDir=<RimWorld folder>`; copy `mod/About` and
   the DLLs above into `<RimWorld>/Mods/RimlinkSpike/`; add `asofwar.rimlinkspike` to
   `ModsConfig.xml`; start the game.
4. Result file: `<save data folder>/rimlink-spike.txt` (on Windows
   `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\`).
