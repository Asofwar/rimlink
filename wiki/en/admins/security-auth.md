---
pageId: admins/security-auth
contentRevision: 1
---

# TLS, authentication and permissions

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

TLS is enabled by default. Set PEM CertificatePath/KeyPath or use a self-signed certificate; distribute its pin through an independent channel. SelfSignedPath selects the self-signed certificate path. Plan certificate rotation in advance.

Steam authentication requires SteamWebApiKey and administrative Steam IDs as appropriate. AllowPassword enables password accounts; AdminLogins assigns initial administrators. ServerPassword is not a replacement for personal authentication.

Give each integration a separate Admin:Tokens entry with Name, Token and Scopes. Names are unique, [a-z0-9-], up to 32 characters; tokens are unique and at least 24 characters. read reads, moderate includes read, chat permits external chat, and admin grants all permissions. Minimize scope.

## Limits and safety {#limits}

The panel uses legacy Admin:ApiToken, not named tokens. Its cookie is HttpOnly/Secure/SameSite=Strict; in-memory sessions end on restart; 5 failures lock an IP for 10 minutes. Cookie mutations require CSRF. Do not use --insecure outside an isolated test. X-Rimlink-Actor is a 1–64-character audit label without controls, not proof of identity.

## See also {#related}

- [REST API reference](./api.md)
- [Panel and moderation](./administration.md)
- [Discord bot](./discord.md)
