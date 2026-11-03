# Guild influence (0.8.0)

Guilds reuse the alliances in the Social window. Influence is an optional map overlay, not a territorial ownership or war system.
It does not grant building, settlement, access or attack permissions. Outposts, capture and declarations of war are outside this feature.

## Publish or withdraw

Open **Settings** in the world map's **Guild influence zones** panel, or **Influence settings** in the alliance section of the Social window.
Choose your colony and press **Publish influence**. The client confirms the loaded colony's actual root surface and current save generation,
then sends explicit consent to the server. Default is unpublished. Visits, live mirrors and colonies on additional planet layers cannot
confirm a surface colony. A colony from an older client stays unpublished until its owner confirms it in a current client.

Publication reveals the colony's alliance name and color to other players. It reveals neither the treasury, member list nor alliance chat.
**Withdraw consent** also works when the server currently says unpublished: it advances the consent version so an older delayed enable
cannot turn publication back on. Unanswered operations remain in the existing outbox and replay their exact original context and version.
An administrative reset, leaving an alliance or disbanding it revokes publication; rejoining does not restore consent.

An ordinary colony archive keeps consent with the archive. Restore rechecks alliance, world, generation and capacity. A changed alliance
cannot inherit old consent. If the public source cap is full, the colony still restores, publication remains off and a notice explains why.
The player can publish again when a slot is free. Resetting a colony revokes its archived consent too.

## Read the map

Each published root-surface colony contributes radius **3 land-neighbor steps**, with strength `4 - distance`. Water is not painted and
stops propagation. Sources in one alliance use their strongest contribution, rather than adding strength. Equal strongest contributions
from different alliances produce a neutral hatched disputed cell. The nearest source is chosen only for the inspection label, with source
id resolving equal distances; it never decides ownership of a dispute.

The alliance owner chooses one of 16 colors. The legend names alliances, marks your own and lets you emphasize one. Clicking a tile keeps
its normal inspection text and adds the influence and nearest source. Roads, rivers and colony markers remain visible. The overlay toggle
is local to each server and does not withdraw publication. Stale data is dimmed and labeled while a refresh is pending. Unsupported or
invalid snapshots show unavailable, without a partial map. Switching games or server epochs discards previous candidates and geometry.

Offline colonies still contribute. At most **128 published active colonies** are allowed, including offline players. Archives do not occupy
active slots. Capacity is global, not per alliance, and a 129th publication is refused atomically. A restored or imported database exceeding
the cap makes the whole layer unavailable until owners or admins disable enough sources; no subset is silently shown.

## Server and admin API

No new configuration switches are needed. The protocol only grows; old clients can continue playing but do not show influence.
The server persists a world UUID, a monotonic public revision and individual consent versions. A process UUID changes on restart.
The client trusts epochs from World.Get and rejects snapshots or finished mesh candidates older than its invalidation high-water mark.

Authenticated gRPC service `rimlink.v1.Influence`: `Get`, `GetSetting`, `Mutate`. Public snapshots are capped at **64 KiB protobuf**;
the explicitly projected REST form is capped at **128 KiB JSON**. Mutations are capped at 2 KiB protobuf and REST bodies at 4 KiB.
The public DTO contains only epochs, revision, algorithm version, represented alliance ids/names/palette indices and source ids/tiles/radii.

| Route | Required scope | Behavior |
|---|---|---|
| `GET /api/admin/influence` | read | complete public snapshot |
| `GET /api/admin/influence/settings/{id}` | admin | private active-colony setting |
| `GET /api/admin/influence/archive/{id}/setting` | admin | private archived-colony setting |
| `POST /api/admin/influence/settings/{id}/disable` | admin | force-disable with context and expected version |
| `POST /api/admin/influence/archive/{id}/disable` | admin | force-disable an archive with context and expected version |
| `POST /api/admin/influence/alliances/{id}/color` | admin | set palette index 0–15 for a guild in the given world |

Admins may disable publication but **cannot enable it for a player**. Token scopes, cookie CSRF, actor labels, rate limits and audit apply.
Receipt identity uses the authenticated token identity rather than the editable audit label. Repeating the same request id and intent returns
the original result without another change or audit entry; reusing the id with a different target, version or operation is refused.

`rimlink-tools admin` commands: `influence`, `influence-setting`, `influence-archive-setting`, `influence-disable`,
`influence-archive-disable`, `influence-color`. Their argument requirements are printed by the CLI. Private setting responses provide the
world/settlement/generation/alliance context and expected version needed by disable requests.

Typed refusals: `InfluenceConflict` (refresh setting), `InfluenceIneligible` (wrong surface/membership), `InfluenceCapacity` (source cap),
`InfluenceInvalid` (invalid input), `InfluenceIntent` (id reused), `InfluenceUnavailable` (complete snapshot cannot be served).
Conflict and eligibility failures never turn consent on automatically. A temporary unavailable response remains eligible for outbox replay.

## Verification

Automated coverage and real two-client evidence are recorded in [the acceptance report](verification/2026-11-02-guild-influence-acceptance.md).
Synthetic offline sources used for cap measurements belong only to an isolated test database, not a public or production server.
