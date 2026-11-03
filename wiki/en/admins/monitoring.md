---
pageId: admins/monitoring
contentRevision: 1
---

# Monitoring and event feed

Applies to: Rimlink 0.8.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

GET /health checks process availability; /metrics exposes metrics. Restrict network access and collect from a trusted host. Administrative /status returns operational state; /actions correlates changes with operators.

SSE GET /api/admin/events uses epoch:seq identifiers. Send Last-Event-ID on reconnect. A gap means restart/buffer overflow lost events: reread current state instead of deriving it from an incomplete feed. Defaults are 1000 buffered events and a ping every 15 seconds.

Monitor free disk space, the age of the last successful backup, login/save/delivery refusals and long live sessions. “Process alive” does not replace colony read/write verification.

## Limits and safety {#limits}

The feed intentionally excludes private chat, wallets, parcels and raids not yet landed. Keep tokens/full saves out of metrics and shared logs. /metrics is application-public: restrict it at the network layer.

## See also {#related}

- [Discord bot](./discord.md)
- [Administrator troubleshooting](./troubleshooting.md)
- [REST API reference](./api.md)
