import { api } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t, tEnum } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { playerLink, stat, input, refreshButton } from '../ui.js';
import { freezeForm } from './economy.js';

const ACTIVE = ['Queued', 'Announced', 'Landed'];

export function render(root) {
  const stats = h('div', { class: 'stats' });
  const freeze = h('div');
  const list = h('div');
  const filter = h('select', { 'aria-label': t('raids.state') }, h('option', { value: '' }, t('raids.all')));
  const reason = input({ placeholder: t('player.reasonOptional') });
  const returnPawns = h('input', { type: 'checkbox', id: 'return-pawns' });
  let raids = [];

  append(root, 
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.raids')), refreshButton(load)),
    stats,
    section(t('raids.freezeTitle'), freeze),
    section(t('raids.list'),
      h('div', { class: 'row' },
        field(t('raids.state'), filter),
        field(t('raids.cancelReason'), reason),
        h('label', { class: 'row', for: 'return-pawns' }, returnPawns, t('raids.returnPawns'))),
      list));
  filter.addEventListener('change', draw);

  function draw() {
    replace(list, table([
      { label: '#', key: 'id' },
      { label: t('raids.attacker'), render: r => playerLink(r.attackerPlayerId, r.attackerName) },
      { label: t('raids.target'), render: r => playerLink(r.targetPlayerId, r.targetName) },
      { label: t('raids.state'), render: r => tEnum('raid', r.state) },
      { label: t('raids.mode'), render: r => `${tEnum('pawnMode', r.pawnMode)} · ${tEnum('timing', r.timing)}` },
      { label: t('player.created'), render: r => fmtDate(r.createdAt) },
      { label: t('raids.resolved'), render: r => fmtDate(r.resolvedAt) },
      {
        label: '',
        render: r => ACTIVE.includes(r.state) ? busyButton(t('raids.cancelConfirm'), async () => {
          const why = reason.value.trim();
          const text = `${returnPawns.checked ? t('raids.cancelReturn') : t('raids.cancelKeep')}${why ? ' ' + t('player.banReasonText', { reason: why }) : ''}`;
          if (!await confirmDialog({ title: t('raids.cancelTitle', { id: r.id }), text, confirmLabel: t('raids.cancelConfirm') })) return;
          await api.post(`raids/${r.id}/cancel`, { reason: reason.value.trim() || null, returnPawns: returnPawns.checked });
          banner(t('raids.cancelled', { id: r.id }), 'info');
          await load();
        }) : null,
      },
    ], filter.value ? raids.filter(r => r.state === filter.value) : raids));
  }

  async function load() {
    try {
      const [s, all] = await Promise.all([api.get('raids/stats'), api.get('raids')]);
      raids = all;
      replace(stats, 
        stat(t('raids.queued'), s.queued),
        stat(t('raids.announced'), s.announced),
        stat(t('raids.landed'), s.landed),
        stat(t('raids.resolvedToday'), s.resolvedToday),
        stat(t('raids.expired'), s.expired),
        stat(t('raids.cancelledCount'), s.cancelled));
      replace(freeze, freezeForm('raids/freeze', s.frozen, s.freezeReason, load, t('raids.freezeConfirm')));
      const chosen = filter.value;
      replace(filter, h('option', { value: '' }, t('raids.all')), [...new Set(all.map(r => r.state))].sort().map(state => h('option', { value: state }, tEnum('raid', state))));
      filter.value = chosen;
      draw();
    } catch (error) {
      showError(error);
    }
  }

  load();
}
