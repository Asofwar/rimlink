import { api } from '../api.js';
import { h, table, busyButton, section, banner } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate, fmtBytes, fmtDuration } from '../../shared/format.js';
import { playerLink, stat, poll, actorName, roleName } from '../ui.js';

const POLL_MS = 10000;

export function render(root) {
  const stats = h('div', { class: 'stats' });
  const online = h('div');
  const recent = h('div');
  root.append(h('h1', null, t('nav.overview')), stats, section(t('overview.online'), online), section(t('overview.recent'), recent));

  async function refresh() {
    const [status, players, actions] = await Promise.all([api.get('status'), api.get('players/online'), api.get('actions?limit=10')]);
    stats.replaceChildren(
      stat(t('overview.players'), t('overview.playersValue', { online: status.playersOnline, max: status.maxPlayers, total: status.playersTotal ?? '—' })),
      stat(t('overview.uptime'), fmtDuration(status.uptimeSeconds)),
      stat(t('overview.world'), status.worldExists ? t('overview.settlements', { count: status.settlements ?? '—' }) : t('overview.noWorld')),
      stat(t('overview.raidsActive'), status.raids?.active),
      stat(t('overview.liveActive'), status.liveActive),
      stat(t('overview.saves'), status.saves ? `${status.saves.count} · ${fmtBytes(status.saves.totalBytes)}` : '—'),
      stat(t('overview.lastBackup'), status.backups?.lastAt ? fmtDate(status.backups.lastAt) : t('overview.never')),
      stat(t('overview.version'), `${status.version} / ${status.protocolVersion}`));

    online.replaceChildren(table([
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

    recent.replaceChildren(table([
      { label: t('actions.at'), render: a => fmtDate(a.at) },
      { label: t('actions.actor'), render: actorName },
      { label: t('actions.action'), key: 'action' },
      { label: t('actions.target'), render: a => a.targetName ?? (a.targetAccountId ? `#${a.targetAccountId}` : '') },
      { label: t('actions.details'), key: 'details' },
    ], actions));
  }

  return poll(refresh, POLL_MS);
}
