# Client diagnostics and server invitations

## Diagnostics

Open **Diagnostics** from the **Multiplayer** server list or the in-game Rimlink overlay.
The window captures a snapshot when opened. Close and reopen it to refresh.
It shows the client/protocol versions, connection state, the last successful save upload's
local time and server save version, the last upload error category, and the live role.
The report also contains reconnect duration, live state, snapshot version, speed/pause source,
pending outbox count and whether a local save is awaiting upload.

Upload history is for the current connection only (including its automatic reconnects), not
persisted across a new login or a game restart. It covers every `SaveTransfer.UploadAsync` path,
including upload of a pending local save at login and live snapshots. A failed upload preserves
the previous success's time/version; a subsequent success clears the error. No exception text
is exported. The version shown is a **save version**, not a server software version. The game
compatibility line is RimWorld 1.7; it does not identify the installed game build.

The scrollable preview is the exact contents of the ZIP. **Save local ZIP** writes a new file
to `<game save-data folder>/Rimlink/Diagnostics/rimlink-report-<UTC time>-<random id>.zip`.
The folder is shown before saving and the complete filename afterwards. Existing reports are
never overwritten. There is no automatic upload, email, endpoint call or folder launch.

The ZIP contains exactly:

- `diagnostics.json`: typed connection/upload/live data and the ordered active mod inventory;
- `events.log`: UTC times and severity of up to 128 Rimlink logger calls in this game process.

Free-form log messages are **entirely omitted**, even when they resemble harmless diagnostics.
The game's `Player.log` is never opened. Arbitrary mod metadata can itself contain private data:
only the fixed official RimWorld/DLC, Harmony and Rimlink package identifiers remain readable.
All other mod identifiers become `anonymous-mod`; externally supplied versions are omitted
(even a numeric version can contain an IP address). Rimlink's version comes from its own
compiled constant. Positions preserve load order. This is a
privacy-preserving inventory, not a complete modpack export for reproducing the game.

Limits: 128 mod entries (with an explicit truncation flag), 128 log entries, 48 KiB per ZIP entry,
64 KiB for the entire ZIP and 64 KiB for the UTF-8 preview. Unrecognized enum values become
`Unknown`. No names, player/session identifiers, login, SteamID, IP, endpoint, certificate pin,
filesystem path, chat, password, token or Steam ticket is copied into the package. Error details,
private mod identifiers and the full game log must be investigated separately with the player's
explicit consent; they cannot be recovered from this report.

## Invitations

After connecting and verifying a server's certificate, choose **Invite** beside that saved server.
Review the address/fingerprint, then **Confirm and copy invitation**. Opening the window alone
does not change the clipboard. Share the resulting plain-text link through a trusted channel:

```text
rimlink://play.example.org:25560?fp=ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789
rimlink://[2001:db8::1]:25560?fp=ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789
```

These are format examples, not running servers or real certificate fingerprints.
The fingerprint is SHA-256 of the server certificate's DER bytes (64 hexadecimal digits).
Export uses the locally trusted fingerprint, never a newly fetched certificate. Without a
valid saved pin, export asks you to connect and verify first.

To import: **Multiplayer → Import server invitation**, paste, **Review invitation**, verify the
address/fingerprint with the owner, then **Add bookmark and trust certificate**. Review is
read-only. Confirmation saves local trust and a bookmark; it never connects, registers an
account, starts a process or downloads a file. Connect using the server list when ready.
Repeated import of the same address/pin does not duplicate the bookmark.

An existing different pin blocks import. It is checked again on confirmation, including trust
changes made after preview. A malformed trust file also blocks writing. Legitimate certificate
changes must be verified through the normal connection certificate warning; invitation import
cannot replace a trusted certificate. Trust data is written through a temporary file and atomic
replacement; a failed write leaves the previous trust and in-memory pin unchanged.

Accepted input is one exact `rimlink://host[:port]?fp=<64 hex digits>` string, at most 512
characters, without spaces, line breaks, percent escapes, extra parameters, credentials, paths
or fragments. DNS names are ASCII (use punycode for international names), labels at most 63
characters, total host at most 253. IPv4 requires all four decimal octets without leading zeros;
IPv6 requires brackets and does not accept scope IDs. Ports are 1–65535, default 25560.
Canonical output lowercases DNS, normalizes IP literals, includes the port and uppercases the
fingerprint. Parsing is pure: no DNS lookup, connection, shell invocation or file access.
There is no OS URL-handler registration; paste invitation text into the game's import window.

## Implementation rulings and verification boundary

This implements the diagnostics/invitation section of the approved
`2026-11-01-rimlink-next-features-design.md`. The old modpack-join spec's “future connection code”
notes are superseded by the approved scope: no optional name/base64 form, public-page generator,
server catalog or automatic pin replacement is introduced. The existing client bookmark and
pin stores suffice; there is no protocol change, server endpoint, database entity or migration.
No new server mutation exists, so no new request ID/outbox operation is needed.

Free-form log text and unknown mod IDs are intentionally omitted instead of attempting unsafe
redaction. This limits support detail but prevents sensitive data hiding in innocent-looking
messages or third-party metadata from reaching an exported archive.

Automated tests cover canonical/malformed IPv4/IPv6, default ports, escaping/oversize input,
pin conflict/reload/stale-preview/concurrent imports/corrupt trust, ZIP contents and preview,
dirty logs, unknown metadata, bounded inventory/logs, real server upload success/refusal/recovery.
Game UI is compiled against RimWorld; a build is not a live two-player or visual validation.
Use the unchecked scenarios in `manual-checklist.md` for the isolated game stand.
