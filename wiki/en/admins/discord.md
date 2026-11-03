---
pageId: admins/discord
contentRevision: 1
---

# Discord bot

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The bot is a separate process/container configured under RimlinkBot. Server.Url points to server HTTPS; ApiToken is a separate named server token with the necessary scopes. StateFile persists the feed position and needs persistent storage.

Create a bot in the Discord Developer Portal, enable the required Message Content intent for chat bridging, and add it to the chosen guild with least permissions. Set Discord.Token, GuildId, Language (en/ru), ModeratorRoleIds, AdminRoleIds and channel IDs for Server/Raids/Market/Moderation/Chat. Zero IDs in the sample are not a working configuration.

Configure CertificateSha256 (pin), or a trusted chain with CertificateName where needed. Test status reads, role restrictions and public chat in a test channel before enabling the bridge.

## Limits and safety {#limits}

Discord.Token and Server.ApiToken are different secrets. Insecure defaults to false; true is only for isolation. Server scopes and Discord roles are independent. The bridge relays public chat, not direct/alliance messages. Restart/gaps require state refresh, not invented missing events.

## See also {#related}

- [TLS, authentication and permissions](./security-auth.md)
- [Monitoring and event feed](./monitoring.md)
- [Chat, messages and pings](../players/chat-pings.md)
