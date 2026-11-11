---
pageId: players/passive-outposts
contentRevision: 13
---

# Passive outposts: workers, stock and recovery

## Availability and preparation {#usage}

Outposts are available on servers where the administrator has enabled and activated them; the setting is off by default. A server option, an administrator or an accepted manifest cannot open that gate. The real two-client G1–G7 acceptance and the single final Astra review have completed. The seven review fixes have been verified with targeted checks and four affected in-game scenarios. The steps below describe the isolated acceptance stand.

An outpost is a server-held workplace without a simulated map. Bring an actual arrived caravan with eligible free adult baseline human colonists to an available tile on the administrator's confirmed complete world manifest. The game/core preserve the workers' exact serialized identity, including supported genes and DLC references. No terrain is generated on the server. Players can use different colony scenarios, storytellers and difficulties on the same planet. Manifest compatibility uses the shared world parameters, the actual game build, and the active DLC and gameplay definitions.

The colony may own one outpost with 1–4 workers. Founding reserves 500 silver from the server wallet and charges it once at commit. The stock holds 1000 individual resource units. Only WoodLog and Steel are supported: wood needs suitable natural trees/biome; steel needs eligible accessible land. Existing settlements, NPC sites, outposts and reservations exclude a tile. The game checks actual local quest/mod world objects before Prepare and again before removing workers. Exceptions cover the selected caravan itself and the exact own Active marker for Assign. Guild influence grants no territorial right.

## Production and management {#production}

Production uses elapsed real time, including offline time. While held on the server, workers do not need food, age, get sick or advance skills. Their skill and health/capacity inputs are captured on departure. The hourly worker rate is 20 wood or 10 steel multiplied by `(0.5 + 0.05 × clampedSkill) × H`, where H is the minimum of consciousness, manipulation and moving, bounded to 0..1 and positive. The server retains exact fractions rather than repeatedly rounding floating-point values.

At capacity production stops, the fractional remainder becomes zero, and no backlog accumulates. Claim returns the whole stock while retaining the fraction. Recalling some workers retains stock and fraction. Recalling the last worker or closing returns all remaining workers and stock, with a warning that the fractional remainder is discarded. Changing resource profile requires an empty stock and explicit confirmation of fraction loss. Closing does not refund the founding charge.

## Delivery and uncertain responses {#recovery}

A recipient with a pending protected parcel already requires a compatible client while custody still belongs to the sender. Login, saving and physical blob retrieval enforce this requirement. An unsupported client receives OutpostClientRequired before the contents are issued.

Delivery to the owner's mailbox is manual, free and immediate on the server; physical receipt happens at home, not inside a caravan. Exact UUID registry, custody lineage, accepted save proofs and delivery receipts prevent a second spawn or credit. An accepted receipt is acknowledgment-only. If a returned worker is sold to a colony with ordinary saves only, the sale keeps that worker in escrow until the buyer submits the first accepted protected save containing the delivery receipt. The worker retains the same server UUID. If compatibility checks fail before placement, the game pauses and blocks saves. The pending parcel stays on the server while the player resolves compatibility.

A Commit or Cancel reply with an intermediate Prepared, RecoveryRequired or cancellation fence retains the original journal until an allowed terminal result. Reply scope, operation ID and digest are checked. Leaving for the menu or changing sessions releases the transient Busy state while preserving the durable outbox. Busy cleanup is bound to the run identity.

If a request is uncertain, preserve its outbox entry, local capture and saves. Read the original result and retry that exact request through the recovery flow. Unknown, an absent flag, a timeout or an old save is not proof that property was never removed. Do not create a replacement worker, resend a gift or reset the colony to work around RecoveryRequired.

After a crash and a new game process, an uncertain operation may block login with RecoveryRequired. The original request and local saves remain available for inspection; that refusal does not confirm cancellation, returned workers or parcel delivery. Loading an old save does not restore continuity of the game branch.

A confirmed rejection of the first staffing request leaves the workers in the caravan and clears only that rejected request when the cause is an unavailable outpost slot or staffing target, an occupied or ineligible tile, an unconfirmed or changed manifest, an ineligible worker, insufficient founding funds, debt, frozen trading or a threat blocking founding. The same rule applies to a first owner command that the server definitively rejects because the outpost or recall selection changed, or because the new profile, stock or fraction confirmation prevents the switch. An older uncertain request, a timeout, an identity mismatch or corrupt recovery evidence still requires recovery.

If the same continuous game branch remains active after an uncertain Prepare and the workers were not removed, normal cancellation first reads the original operation and requests a fresh cancellation fence. The first request keeps the original PreRemoval proof in its durable journal, bound to the original operation. Only when obtaining the first fence may this proof be an accepted historical save: reconnect may already have advanced the canonical save. This does not yet prove that no removal occurred. The game then saves its actual current state with the NoRemoval receipt for the new fence and waits for the server to accept that fresh current proof; only then does cancellation complete. RecoveryRequired alone may merely retain the original operation and does not authorize a receipt for a different transition.

Cancellation after removal can select only the existing verified one-batch return; ReturnAfterRemoval still requires a current accepted proof. Positive committed/accepted outcomes win races. Ordinary transfer, raid and live-session return fences survive acceptance, cleanup, archive, rollback and restore.

## Limits and lifecycle {#limits}

Disabling new outposts never disables protection of existing history. An incompatible client cannot overwrite protected metadata or receive an unsupported payload; unrelated legacy colonies retain their existing behavior. Missing or corrupt authority/evidence keeps protected work unavailable.

Archive pauses production. Reset requires the outpost closed, all physical receipts accepted and custody resolved. A rollback preserves the original proof bytes and advances only the current authorized view; it does not roll back earned stock or permit another grant. Bulk restore requires explicit stopped-server procedures and a fresh accepted capture after the authorized canonical download. An administrator may inspect or select proved recovery outcomes, but cannot mark a payload accepted, clear a fence or invent an absent source.


Since 0.10.0 the common save envelope is extended with separate guild field12 and independent GuildVersion. Personal registries/receipts are retained in full, and a mixed save requires both families. Manifest reads share bounded page/hash checks, while access to a guild catalog is tied to its fixed parcel.

If the server refuses a guild resource request because the owner changed, the stock is insufficient, or the recipient changed, refresh the outpost details and submit a new request. A confirmed refusal removes the old request from automatic retry. Temporary server unavailability leaves it queued for reconciliation.

To open a guild battle you need the same game and Rimlink builds as the server's arbiter and the same mods in the same order, that is, the server's modpack with nothing added. Where the game and the mods are installed does not matter: an ordinary Steam installation with Workshop mods qualifies. Personal mod settings do not get in the way. On a mismatch the client does not open the battle and stays in its own colony.

To toggle combat readiness in a guild arena, select one of your fighters and choose **Draft selected fighter** or **Undraft selected fighter**. After the arbiter executes the order, the game displays the updated fighter status.

If the battle has started, open your home colony and choose **Join battle** again. The game loads the mirror, displays the latest frame and restores control for the assigned commander. If the assigned defender joins late, AI controls the defence until control is transferred.

The server holds up to 512 pending guild parcels across the world. At capacity, it refuses a new claim before deducting stock. Submit a new request after some parcels have been received.

## See also {#related}

- [Administrative readiness and exact commands](../admins/passive-outposts.md)
- [Saves and recovery](./saves-recovery.md)
- [World map](./world.md)
