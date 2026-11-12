---
pageId: admins/security-auth
contentRevision: 18
---

# TLS, authentication and permissions

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

TLS is enabled by default. Set PEM CertificatePath/KeyPath or use a self-signed certificate; distribute its pin through an independent channel. SelfSignedPath selects the self-signed certificate path. Plan certificate rotation in advance.

Steam authentication requires SteamWebApiKey and administrative Steam IDs as appropriate. AllowPassword enables password accounts; AdminLogins assigns initial administrators. ServerPassword is not a replacement for personal authentication.

Give each integration a separate Admin:Tokens entry with Name, Token and Scopes. Names are unique, [a-z0-9-], up to 32 characters; tokens are unique and at least 24 characters. read reads, moderate includes read, chat permits external chat, and admin grants all permissions. Minimize scope.

A connection has 30 seconds to authenticate. Successful REST token and scope checks, or a valid panel session with the required CSRF check, mark the connection authenticated before the operation executes. This allows a long-running administrative operation to finish. Credentials and permissions are checked on every request.

## Separate and revoke integration access {#rotate-access}

Give monitoring read, a chat bridge read+chat, and moderation moderate only as needed. moderate includes read but not chat; admin grants everything. Use a distinct name/secret per process. Revoke independently, apply configuration through controlled restart and verify the revoked token fails.

Only legacy Admin:ApiToken opens the panel; a named admin-scope token still does not. Restart ends cookie sessions. Cookie mutations require X-Rimlink-Panel: 1 and an acceptable Origin; Bearer integrations must not emulate cookies.

Never send tokens in URLs. CLI --token can remain in history/process lists: use a protected operator environment and keep command transcripts private.

## Planned certificate replacement {#rotate-cert}

Preserve working settings and announce the new pin through a trusted channel before replacement. After restart compare the served certificate and test game clients/bot. A stored old pin must require a decision, not update silently.

For an unexpected pin, inspect address/NAT/proxy and whether rotation really occurred. Never tell everyone to use --insecure or trust arbitrary certificates. The default SelfSignedPath depends on DataDir; an empty mount can look like certificate rotation on a new server.

In guild battle development, issuing/renewing a control grant is a separate authenticated player mutation with server-derived side and current session/membership binding. ReconcileControl is limited to two RPCs per five seconds per account, including retries; a new financial grant is additionally limited to once per five seconds per side. This is not the battle order limit: the approved hot transport of 20 commands/s per side is being wired separately. The capability stays closed until the administrator enables it.

The WIP battle channel additionally binds to the specific HTTP/2 connection ID, current session/membership, capability digest and server connection epoch. Reusing bearer/capability on another connection requires channel replacement and CutApplied. The hot path performs no financial commit: its short-lived read-only SQLite reader closes before S/B release, and the cache retains no DB/file handles. The defaults stay off until the administrator enables them.

The new executor Hello separates Execution and scoped RecoveryDelivery. A nonce is one-use with a monotonic 30-second TTL; its binding includes server boot and current credential generation. Cached replies do not extend old leases. HelloBegin is limited to 2/5s and HelloComplete to 4/5s. Runtime ID is neither a credential nor hardware attestation: the trusted shipping adapter measures native facts. Historical-profile cold Final never bypasses current credential revocation or admits a new simulation.

## Limits and safety {#limits}

The panel uses legacy Admin:ApiToken, not named tokens. Its cookie is HttpOnly/Secure/SameSite=Strict; in-memory sessions end on restart; 5 failures lock an IP for 10 minutes. Cookie mutations require CSRF. Do not use --insecure outside an isolated test. X-Rimlink-Actor is a 1–64-character audit label without controls, not proof of identity.

## See also {#related}

- [REST API reference](./api.md)
- [Panel and moderation](./administration.md)
- [Discord bot](./discord.md)


The arbiter transport uses a separate authorization scheme, RimlinkExecutor, with its own secret. A player/administrator Bearer session cannot enter this service, and an executor credential cannot access Wallet or other player RPCs. The server compares the secret SHA-256 in constant time, checks identity/profile/lease, and refuses plaintext requests when TLS enforcement is enabled. Heartbeats publish current availability without changing asset authority; a new lease does not inherit the old one, and a cleanup hold blocks a new Start. Real native qualification and result application remain unfinished.

Execution and RecoveryDelivery use a dedicated authenticated Hello challenge binding runtime/profile/build/inventory/world/boundary and server boot. Initial Entry proof forbids a Game, player session, battle/tick context, or pending callbacks. A new courier runtime receives only delivery scope, not permission to replay simulation or attest Entry for the original process. Tagged cleanup requires a fresh challenge and durable rejection of delayed assignment work.

The narrow StartReplayOnly admission is for the original live runtime after a lost Start authorization: the original UUID and exact initial are mandatory. It permits no new Prepare/proposals/commands. RuntimeStarted proof is recorded in the existing transport journal without another financial Start. Native/restore qualification of this flow is still incomplete.

Guild acceptance login requires explicit mode/stand ID agreement on both sides. Core and GABS `rimlink/connect` accept a separate `acceptanceStandId` plus `acceptanceCertificateSha256` selection, only for HTTPS loopback IPs. The exact certificate is checked independently of the ordinary pin store, with no automatic trust of a new pin. The default client does not accept such a stand automatically. The arbiter uses `AcceptanceStandId` in its own explicit configuration; player sessions are forbidden in that process. An external marker, a wrong source or missing protected provenance cannot grant admission. After authentication, Final returns a retryable `Unavailable` while a dispatched command or control cut awaits evidence. The executor continues submitting confirmations through its authenticated channel.
