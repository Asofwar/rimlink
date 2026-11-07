---
pageId: admins/security-auth
contentRevision: 5
---

# TLS, authentication and permissions

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

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

## Limits and safety {#limits}

The panel uses legacy Admin:ApiToken, not named tokens. Its cookie is HttpOnly/Secure/SameSite=Strict; in-memory sessions end on restart; 5 failures lock an IP for 10 minutes. Cookie mutations require CSRF. Do not use --insecure outside an isolated test. X-Rimlink-Actor is a 1–64-character audit label without controls, not proof of identity.

## See also {#related}

- [REST API reference](./api.md)
- [Panel and moderation](./administration.md)
- [Discord bot](./discord.md)
