import { api } from '../api.js';
import { h, table, busyButton, section, banner, append, replace } from '../../shared/dom.js';
import { t, tEnum } from '../../shared/i18n.js';
import { fmtDate, fmtBytes, fmtDuration } from '../../shared/format.js';
import { playerLink, stat, poll, actorName, roleName } from '../ui.js';

const POLL_MS = 10000;

export function render(root) {
  const stats = h('div', { class: 'stats' });
  const facts = h('p', { class: 'facts-line' });
  const online = h('div');
  const recent = h('div');
  append(root, h('h1', null, t('nav.overview')), stats, facts, section(t('overview.online'), online), section(t('overview.recent'), recent));

  async function refresh() {
    const [status, players, actions] = await Promise.all([api.get('status'), api.get('players/online'), api.get('actions?limit=10')]);
    // What the owner checks first comes first: players, anything frozen, fights in progress, the last backup.
    const economyFrozen = status.economy?.frozen;
    const raidsFrozen = status.raids?.frozen;
    replace(stats,
      stat(t('overview.players'), t('overview.playersValue', { online: status.playersOnline, max: status.maxPlayers, total: status.playersTotal ?? '—' })),
      stat(t('nav.economy'), economyFrozen ? t('public.frozen') : t('public.running'), economyFrozen ? 'warn' : ''),
      stat(t('nav.raids'), raidsFrozen ? t('public.frozen') : t('overview.raidsValue', { count: status.raids?.active ?? '—' }), raidsFrozen ? 'warn' : ''),
      stat(t('overview.liveActive'), status.liveActive),
      stat(t('overview.lastBackup'), status.backups?.lastAt ? fmtDate(status.backups.lastAt) : t('overview.never'), status.backups && !status.backups.lastAt ? 'warn' : ''),
      stat(t('overview.saves'), status.saves ? `${status.saves.count} · ${fmtBytes(status.saves.totalBytes)}` : '—'),
      stat(t('overview.world'), status.worldExists ? t('overview.settlements', { count: status.settlements ?? '—' }) : t('overview.noWorld'), status.worldExists ? '' : 'warn'));
    facts.textContent = t('overview.facts', { version: status.version, protocol: status.protocolVersion, uptime: fmtDuration(status.uptimeSeconds) });

    replace(online, table([
      { label: t('player.name'), render: p => playerLink(p.playerId, p.name) },
      { label: t('player.role'), render: p => roleName(p.role) },
      { label: t('player.ip'), key: 'ip' },
      { label: t('player.since'), render: p => fmtDate(p.since) },
      {
        label: '',
        render: p => h('div', { class: 'row' },
          busyButton(t('player.kick'), async () => {
            await api.post(`players/${p.playerId}/kick`, {});
            banner(t('player.kicked', { name: p.name }), 'info');
            await refresh();
          }),
          busyButton(t('overview.mute30'), async () => {
            await api.post(`players/${p.playerId}/mute`, { minutes: 30 });
            banner(t('player.muted', { name: p.name }), 'info');
          })),
      },
    ], players, t('overview.nobody')));

    replace(recent, table([
      { label: t('actions.at'), render: a => fmtDate(a.at) },
      { label: t('actions.actor'), render: actorName },
      { label: t('actions.action'), render: a => tEnum('action', a.action) },
      { label: t('actions.target'), render: a => a.targetName ?? (a.targetAccountId ? `#${a.targetAccountId}` : '') },
      { label: t('actions.details'), key: 'details' },
    ], actions));
  }

  return poll(refresh, POLL_MS);
}
