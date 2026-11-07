---
pageId: admins/discord
contentRevision: 5
---

# Discord bot

Applies to: Rimlink 0.9.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

The bot is a separate process/container configured under RimlinkBot. Server.Url points to server HTTPS; ApiToken is a separate named server token with the necessary scopes. StateFile persists the feed position and needs persistent storage.

Create a bot in the Discord Developer Portal, enable the required Message Content intent for chat bridging, and add it to the chosen guild with least permissions. Set Discord.Token, GuildId, Language (en/ru), ModeratorRoleIds, AdminRoleIds and channel IDs for Server/Raids/Market/Moderation/Chat. Zero IDs in the sample are not a working configuration.

Configure CertificateSha256 (pin), or a trusted chain with CertificateName where needed. Test status reads, role restrictions and public chat in a test channel before enabling the bridge.

## Start the bot with least privilege {#bot-accept}

Create separate server and Discord bot tokens; fill nonzero guild/channel IDs and role IDs. Server scopes govern API access; Discord roles govern guild command callers. Matching role names create no server permission.

Test status and command refusal for ordinary members in a test channel first. Then enable public bridging and verify direction plus exclusion of direct/alliance chat. Message Content requires the Developer Portal intent. Persist StateFile so restart retains feed position.

For 401/403 fix token/scope instead of granting admin immediately. For TLS inspect pin/name/chain; Insecure=true is not a production fix.


### Complete source-build launch path

1. Use a matching Rimlink checkout, the SDK from global.json, a reachable HTTPS server and a Discord application. Enable Message Content in the Developer Portal; invite with bot and applications.commands scopes and View Channels, Send Messages, Add Reactions, Read Message History permissions. Do not grant Administrator.
2. Publish to a separate directory, not over a running bot:

~~~powershell
dotnet publish src/Rimlink.Discord -c Release -o ./build/discord-bot
Copy-Item ./deploy/discord.appsettings.example.json ./build/discord-bot/appsettings.json
~~~

3. Use the [configuration sample](https://github.com/Asofwar/rimlink/blob/main/deploy/discord.appsettings.example.json) to populate RimlinkBot in the copied appsettings.json. It is read **beside the DLL**, not from an arbitrary working directory. Replace Url, both tokens, GuildId, roles and channels; Channels.*=0 disables that group. Set an absolute StateFile in persistent writable storage and a verified CertificateSha256 or trusted chain/name. Never commit this populated secret file.
4. Minimum server scopes: read for events and /status, /online, /market; read + chat for the two-way public bridge; moderate + chat adds /player and moderation; admin only if administrative commands are required. Configure Discord roles separately. The server Admin:Tokens value must match the bot Server:ApiToken.
5. Environment variables can override settings. Pass secrets through protected service environment, not shell history; these overrides are nonsecret:

~~~powershell
$env:RimlinkBot__Server__Url = 'https://play.example.com:25560'
$env:RimlinkBot__StateFile = 'E:/RimlinkData/discord/feed-state'
dotnet ./build/discord-bot/rimlink-discord.dll
~~~

6. Expected: the process stays running, /status answers in the test guild, an ordinary member cannot /kick, a two-way public test message works with chat scope, and direct/alliance chat does not appear. Missing tokens stop startup validation; resolve 401/403/TLS settings instead of disabling protection.
7. Stop with Ctrl+C in this console or the service manager. Preserve appsettings.json and StateFile before updating; starting with the same StateFile resumes the feed. On a gap, refresh current state instead of inventing missing events. Do not delete state to fix authentication.

This is a launch procedure, not evidence of a performed live Discord connection.

## Limits and safety {#limits}

Discord.Token and Server.ApiToken are different secrets. Insecure defaults to false; true is only for isolation. Server scopes and Discord roles are independent. The bridge relays public chat, not direct/alliance messages. Restart/gaps require state refresh, not invented missing events.

## See also {#related}

- [TLS, authentication and permissions](./security-auth.md)
- [Monitoring and event feed](./monitoring.md)
- [Chat, messages and pings](../players/chat-pings.md)
