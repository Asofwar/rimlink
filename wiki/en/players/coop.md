---
pageId: players/coop
contentRevision: 4
---

# Persistent shared colony

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

1. The owner opens **Shared colony** below chat and invites a partner.
2. The partner opens the same window, refreshes and accepts.
3. The owner assigns free colonists on the home map.
4. From their own home colony, the partner chooses **Join colony**. Their home is saved before loading the mirror.
5. The partner controls assigned pawns, including work, schedules, equipment and combat; the owner continues playing and writes the only canonical save.

Membership survives restart. Changing assignments or revoking membership ends the current session. The partner leaves to return home.

## Who controls what after an invitation? {#coop-check}

Invitation, acceptance, pawn assignment and joining are four separate steps. Membership alone does not grant control of the settlement. The owner must assign free colonists on the chosen home map; the partner must start from their own home colony.

For a first check, assign one pawn and leave another unassigned. Have the partner change the assigned pawn's work/schedule and verify execution on the host. The unassigned pawn must not accept partner orders. Leave and rejoin: membership persists, pawns remain with the host, and the partner receives no “return” parcels containing host colonists.

Assignment grants real trust over equipment and combat orders. Do not give it merely for a tour; use an ordinary/live visit instead.

## Revocation, busy sessions and reassignment {#coop-recovery}

One partner can join at a time; eight invitations/members and 32 assigned pawns are storage/permission limits, not concurrent-client counts. A busy refusal is not fixed by a second account.

Reassignment or revocation ends current coop. Rollback preserves membership but clears assignments: an old numeric pawn ID may identify a different object. Reset removes colony permissions; archive/restore preserves membership but ends active live sessions.

If the owner leaves, there is no autonomous continuation. A retained mirror is not a second canonical save. Administrators can inspect/revoke membership, not fix a session by granting pawn copies.

## Limits and safety {#limits}

One concurrent partner, up to 8 invitations/members and 32 pawns. The owner must be online at home. Wallet, transfer and admin permissions are not shared. Reset removes membership; archive/restore preserves it; rollback keeps membership but clears assignments. Owner pawns are not mailed to the partner.

## See also {#related}

- [Live: the active map](./live.md)
- [Panel and moderation](../admins/administration.md)
- [Saves and recovery](./saves-recovery.md)
