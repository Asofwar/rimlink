---
pageId: players/connect
contentRevision: 7
---

# Connecting and signing in

Applies to: Rimlink 0.9.2 · RimWorld 1.7 · protocol 1.

<img class="wiki-art" src="/illustrations/connect.svg" alt="Two colonies connect to the server over a secure connection." width="960" height="440" />
<p class="wiki-caption">Two colonies connect to the server over a secure connection.</p>

## Usage {#usage}

<ol class="flow-diagram" aria-label="Process diagram"><li><strong>Endpoint and TLS</strong><span>Verify host and fingerprint.</span></li><li><strong>Personal login</strong><span>Steam or an allowed password account.</span></li><li><strong>Collection</strong><span>Diff → approved install → restart → check.</span></li><li><strong>Colony</strong><span>Current server save or a new colony.</span></li></ol>

Add the host and port in **Multiplayer** (default 25560). Obtain the address from the administrator; the browser panel URL and game endpoint need not match. A server-wide password, if configured, is additional to personal authentication.

Steam login uses a Steam ticket. Password login is available only when enabled by the server; credentials belong to that server. A new certificate is remembered after trust is granted. If its pin changes, confirm the certificate replacement with the administrator first.

After login the server loads your saved colony, offers a new colony, or shows modpack differences. Resolve the listed differences and reconnect.

The current source also checks the mods listed in your own save header. Inactive mods stop login with a list of names before the map loads or recovery operations run. The check also precedes uploading a local recovery save. Enable the required mods and reconnect; do not delete the save or recovery journal. This fix has not shipped in 0.9.2.

After an administrator rolls back the colony, return to the main menu and sign in again. The client downloads the selected server save, keeps its original custody proof and saves future progress in the new write epoch. A newer local pending save is kept separately.

## Login steps: certificate, account, colony {#login-flow}

1. Open a saved server or enter the exact host/port. A browser panel URL can differ from the game endpoint.
2. On first TLS contact, compare the fingerprint with the administrator through another channel. Trust only after verification. A changed pin requires a separate stop, not an automatic continue.
3. Enter the shared server password if enabled. It does not replace a Steam ticket or personal password.
4. For Steam, wait for ticket verification; temporary rejection may retry automatically. Password accounts belong to this server, which must allow that authentication method.
5. Complete collection checks. The server then loads the existing colony or offers a new one. If you expected an old colony, check account/address before creating anything.

**Success:** the home map is loaded, connection active, with no unfinished recovery. Public player names and account logins are not interchangeable.

## Distinguish temporary waiting from a final refusal {#login-refusal}

“Waiting for Steam to accept the login” is a Steam wait, not save corruption. “login details are invalid” requires checking credentials. “This account logged in from another place” fences this copy: do not continue it as an independent timeline.

A normal drop pauses the game and reconnects: first after a second, then less often, up to once a minute. After two minutes you may wait or return to the menu; retries continue behind the dialog. A final refusal, such as a ban or untrusted replacement certificate, requires the menu and fixing its cause. Preserve local state rather than replacing it with another colony.

## Limits and safety {#limits}

Do not disable TLS to work around certificate errors. Never send personal passwords in chat. For a temporary Steam refusal, let authentication retry rather than creating many accounts.

## See also {#related}

- [Server modpacks](./modpacks.md)
- [TLS, authentication and permissions](../admins/security-auth.md)
- [Player troubleshooting](./troubleshooting.md)
