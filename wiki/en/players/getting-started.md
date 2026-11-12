---
pageId: players/getting-started
contentRevision: 10
---

# Your first session

Applies to: Rimlink 0.10.4 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. Check the game version, install Harmony and Rimlink, enable them in the mod list and restart.
2. Open **Multiplayer**, add the server address and port, then authenticate using a method enabled by its administrator.
3. Verify a new certificate fingerprint through an independent channel. Resolve modpack differences.
4. Create a colony on the shared planet: the server controls world parameters; choose a free site.
5. Wait for colonists to arrive and play. Leave through the normal main-menu action and let saving/upload complete.
6. On the next login, confirm that this colony loads before starting transfers or travel.

## First evening: from server address to a second login {#first-evening}

Have the host/port, optional server password, DLC/mod requirements and an administrator contact for fingerprint verification ready. A server password gates the server; a personal password, where allowed, belongs to your account.

1. Verify **Multiplayer** exists in the main menu. If absent, fix installation before networking.
2. Add the server, verify TLS and sign in. Finish modpack validation before creating a colony.
3. If the planet has not been initialized, wait for the administrator. Otherwise choose a scenario and a free tile on the server planet.
4. Once colonists arrive, pause and check the player name, home map and other settlements.
5. Use the normal return-to-menu action and let save/upload finish. Join the same server with the same account.
6. The same colony should load. Test this before trading or visiting, so the basic save path is known to work.

**Outcome:** one account, one settlement and a server save demonstrated by a second load. An unexpected new-colony screen is a reason to verify server/account, not create a replacement.

## If the first login does not finish {#first-failure}

Separate stages: missing menu means installation; no response means address/port/reachability; changed pin means security; mismatch means collection; login refusal means account; load failure means save/dependencies. Record the exact message and stage. Do not create another account to “recover” goods after a drop.

For a first safe exchange, use a small quantity of an ordinary resource and check both mailboxes. This is not a load test or a reason to send valuable colonists before validating the collection.

## Limits and safety {#limits}

Do not force-kill the process during upload. An initial administrator login may create the world; ordinary players must not bypass it by creating a separate planet.

## See also {#related}

- [Installing the mod](./install.md)
- [Connecting and signing in](./connect.md)
- [Saves and recovery](./saves-recovery.md)
