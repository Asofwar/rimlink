---
pageId: players/chat-pings
contentRevision: 6
---

# Chat, messages and pings

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Public chat is visible to server participants. A direct message targets a specific player. Alliance chat is available to current alliance members; check the channel before sending.

Text such as `[[tile:123]]` creates a location card. A ping points to a world tile or coordinates on the active map; a visit/live counterpart may be the default recipient. Use pings for coordination, not secret data.

Blocking a player prevents messages, gifts, trades and pings between you. Notification settings and letter-triggered pauses are separate controls.

### Send a ping

Point at the location and press **G**; change the binding in **Options → Keyboard → Rimlink**. On the world map this marks a tile, on a colony map a cell. A visit/live session can use the current counterpart as recipient. Typing in a text field never sends a ping. The recipient sees a temporary marker (8 seconds); if absent, check recipient, blocks and the 12-per-minute limit. For a persistent chat location card send `[[tile:123]]` with the intended tile number.

## Public, direct and alliance channels: who receives a message? {#channels}

Check channel and recipient before sending. Public chat reaches server participants and, if configured, the public Discord bridge. Direct messages target one player. Alliance chat is for current members; visible history begins at your join time and is not bridged to Discord.

A [[tile:123]] card stays in the message and opens a location; G creates an eight-second marker, not a persistent note. On a colony map it marks a cell; on the planet it marks a tile. Never put passwords, tokens or private data in public chat.

If a message/ping fails, check recipient, blocks in either direction, mute and rate limits. Blocking also prevents new gifts/trades and cancels existing mutual trades, but is not a raid or market ban.

## Limits and safety {#limits}

Public chat may be relayed to Discord. Blocking does not disable raids, markets or visits. Alliance chat and direct messages are not part of the public Discord bridge.

## See also {#related}

- [Profiles, friends and alliances](./community.md)
- [Live: the active map](./live.md)
- [Discord bot](../admins/discord.md)
