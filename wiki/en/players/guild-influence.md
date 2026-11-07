---
pageId: players/guild-influence
contentRevision: 5
---

# Guild influence

Applies to: Rimlink 0.9.2 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Open **Guild influence zones → Settings** on the world map or influence settings in your alliance. Select your home root-surface colony and explicitly publish influence. This reveals alliance name and color, not treasury, membership or chat. Publication is off by default.

The radius is 3 adjacent land steps; strength is 4 − distance. Water blocks propagation. Contributions from one alliance use their maximum, not their sum; equal strength from different alliances produces neutral hatching. The alliance owner chooses one of 16 colors. Offline sources still count.

The local overlay toggle does not withdraw consent. Use the separate withdrawal action: it advances consent version even when already unpublished, preventing a delayed enable request.

## Publish, hide the layer, or withdraw consent? {#publish-influence}

1. Join an alliance and use an eligible home root-surface colony.
2. Open influence settings on the world map. Review settlement/alliance context and explicitly enable publication; it defaults off.
3. Inspect source/color on the map. Radius is three land steps, not a circle across water. One alliance contributes its maximum strength; ties between alliances use neutral hatching.
4. The local layer button hides only your view. To stop publication, **withdraw consent**. This advances its version even when already disabled, protecting against delayed enable requests.

The alliance owner selects one of 16 colors. Offline status does not remove a source. This grants no land access rights, color-based resources, guild war or outposts.

## Why is a source refused or the overlay unavailable? {#influence-refusal}

Check membership, current settlement, root surface and colony lifetime. Reset/leaving/disbanding revoke consent; archive restore rechecks context and capacity. A stale enable must not apply to a new colony.

The server permits 128 active sources. An imported database above that limit makes the whole overlay unavailable instead of arbitrarily hiding some sources. Refresh and contact the administrator, who may disable a source but cannot consent for a player. Missing color does not mean losing a settlement.

## Limits and safety {#limits}

This is not ownership, war, outposts or access permission. The server-wide cap is 128 active sources. An over-cap imported database makes the entire overlay unavailable. Reset/leaving/disbanding revokes consent; archive restore rechecks context and capacity. Administrators can disable, not enable on a player's behalf.

## See also {#related}

- [Profiles, friends and alliances](./community.md)
- [Shared world and settlements](./world.md)
- [REST API reference](../admins/api.md)
