---
pageId: players/community
contentRevision: 4
---

# Profiles, friends and alliances

Applies to: Rimlink 0.9.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

In **Players → Social**, edit your biography, invitation preferences and notifications. Find a player, request friendship, or create/join an alliance. Its owner invites members, transfers ownership or disbands it. Alliance chat is visible to current members from their join time and is not relayed to Discord.

The treasury accepts wallet contributions. The owner sets daily withdrawal allowances; spending is counted per UTC day and does not reset on leaving/rejoining. Leaving does not refund contributions; disbanding pays the entire balance to the current owner. Transfer ownership deliberately.

Allies are protected against mutual harmful attacks and incidents. Joining with conflicting queued hostility may be refused.

## Profiles, invitations and alliances: managing consent {#social-consent}

**Players → Social** opens a directory including offline players. Edit biography and invitation permission in your profile. Public fields include last activity/preferences, so put no secrets there.

Friendship/alliance membership requires invitation and acceptance. Senders can revoke, recipients refuse; closing invitations revokes waiting ones. A block in either direction removes friendship and pending invitations. Unblocking does not restore consent.

The alliance owner invites, grants treasury limits, transfers ownership and disbands. An owner must transfer or disband before leaving. Allies cannot launch harmful attacks against each other; joining may fail while mutual hostility is unresolved. Leaving does not erase an existing operation or its escrow.

## Treasury: a contribution is not a personal deposit {#treasury}

Only wallet silver enters the treasury. Deposit physical stacks first. Treasury withdrawal credits a member's wallet; obtaining stacks still requires normal wallet withdrawal and mail.

Every member, including the owner, has permission and a UTC daily cap. New members receive no automatic withdrawal right. Leaving/rejoining and cap edits do not reset today's spending. Contributions create no refund entitlement: leaving pays nothing automatically, and disbanding sends **the entire balance to the current owner**.

Treasury wealth counts once for its owner. Withdrawal and ownership transfer with a nonempty balance check every member for debt/freeze/threat; an owner/member block also prevents withdrawal. Resolve unknown responses through the original outbox replay. The last 100 audit actions help reconcile results; do not manually repeat a transfer.

## Limits and safety {#limits}

Biography: 500 characters; directory: up to 200 players; 100 friends, 16 alliance members, one alliance per player. Invitations expire in 7 days with up to 32 incoming/outgoing. Transactions cap at 1 billion, treasury at 1 trillion. Members' freeze/debt/threat state may block withdrawal.

## See also {#related}

- [Chat, messages and pings](./chat-pings.md)
- [Guild influence](./guild-influence.md)
- [Wallet, mail and market](./economy.md)
