---
pageId: players/community
contentRevision: 11
---

# Profiles, friends and alliances

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

In **Players → Social**, edit your biography, invitation preferences and notifications. Find a player, request friendship, or create/join an alliance. Its owner invites members, transfers ownership or disbands it. Alliance chat is visible to current members from their join time and is not relayed to Discord.

The treasury accepts wallet contributions. The owner sets daily withdrawal allowances; spending is counted per UTC day and does not reset on leaving/rejoining. Leaving does not refund contributions; disbanding pays the entire balance to the current owner. Transfer ownership deliberately.

Allies are protected against mutual harmful attacks and incidents. Joining with conflicting queued hostility may be refused.

## Guild outposts in the development build {#guild-outposts}

The **Guild outposts** window is available in your home colony when the server grants guild access. It lists outposts and stock retained from lost outposts. Leaders can review ownership terms for an unowned site, choose a UTC defence window and confirm those exact terms. They can also nominate an attacker from their guild and declare an attack. The server returns the scheduled time. Resource requests from active and retained stock go to the leader's current home colony through ordinary delivery.

The same window shows active campaigns for your guild, the scheduled time in UTC and your side's commander. Before Start, the defending leader can select **Assign defender**, choose a member of the guild and confirm the choice.

When a battle is prepared, the assigned commander can select **Open prepared battle**. The mod checks the approved game and mod files, saves the home colony and opens the battle map only after the server accepts that save. The attacker confirms readiness separately on the battle map.

These controls are in the development build. The public build does not offer them yet.

If an operator stops a started battle, its map shows an operator stop without a winner. Return to the same home colony using the normal return button.

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

In guild capture, after V3 activation, creation, joining, leaving, and leader transfer use common authority. Leader transfer preserves the guild object and accepted consent. Disbanding with unresolved resources, objects, or campaigns is currently refused before effects and treasury refund; safe disband issuance is unfinished. This is not an enabled gameplay feature.

## See also {#related}

- [Chat, messages and pings](./chat-pings.md)
- [Guild influence](./guild-influence.md)
- [Wallet, mail and market](./economy.md)
