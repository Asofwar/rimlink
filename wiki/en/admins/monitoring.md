---
pageId: admins/monitoring
contentRevision: 6
---

# Monitoring and event feed

Applies to: Rimlink 0.10.0 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

GET /health checks process availability; /metrics exposes metrics. Restrict network access and collect from a trusted host. Administrative /status returns operational state; /actions correlates changes with operators.

SSE GET /api/admin/events uses epoch:seq identifiers. Send Last-Event-ID on reconnect. A gap means restart/buffer overflow lost events: reread current state instead of deriving it from an incomplete feed. Defaults are 1000 buffered events and a ping every 15 seconds.

Monitor free disk space, the age of the last successful backup, login/save/delivery refusals and long live sessions. “Process alive” does not replace colony read/write verification.

## Three monitoring layers {#monitor-layers}

Separate process availability, application correctness and recoverability. /health covers the first; status/metrics, login/save refusals, queue/session duration cover the second. Successful-backup age and periodic isolated restore cover the third.

Compare disk trends with actual saves.diskBytes; old versions and external ZIPs can grow for different reasons. Preserve time/version/audit before intervention. Avoid private names/tokens in metrics and restrict public /metrics at the network.

## How to handle an SSE gap {#sse-gap}

Persist the last epoch:seq after processing its event and send Last-Event-ID on reconnect. Gap means the local journal is incomplete: reread status/lists, refresh your view and continue the new stream. Never invent missing events or execute mutations from replayed notifications.

A ping proves stream activity, not a new trade. Private chat/wallets/parcels and not-yet-landed raids are intentionally absent; investigate through authorized entity reads, not reconstruction from public feed.

## Limits and safety {#limits}

The feed intentionally excludes private chat, wallets, parcels and raids not yet landed. Keep tokens/full saves out of metrics and shared logs. /metrics is application-public: restrict it at the network layer.

## See also {#related}

- [Discord bot](./discord.md)
- [Administrator troubleshooting](./troubleshooting.md)
- [REST API reference](./api.md)
