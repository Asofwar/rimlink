---
pageId: players/guild-influence
contentRevision: 1
---

# Guild influence

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Open **Guild influence zones → Settings** on the world map or influence settings in your alliance. Select your home root-surface colony and explicitly publish influence. This reveals alliance name and color, not treasury, membership or chat. Publication is off by default.

The radius is 3 adjacent land steps; strength is 4 − distance. Water blocks propagation. Contributions from one alliance use their maximum, not their sum; equal strength from different alliances produces neutral hatching. The alliance owner chooses one of 16 colors. Offline sources still count.

The local overlay toggle does not withdraw consent. Use the separate withdrawal action: it advances consent version even when already unpublished, preventing a delayed enable request.

## Limits and safety {#limits}

This is not ownership, war, outposts or access permission. The server-wide cap is 128 active sources. An over-cap imported database makes the entire overlay unavailable. Reset/leaving/disbanding revokes consent; archive restore rechecks context and capacity. Administrators can disable, not enable on a player's behalf.

## See also {#related}

- [Profiles, friends and alliances](./community.md)
- [Shared world and settlements](./world.md)
- [REST API reference](../admins/api.md)
