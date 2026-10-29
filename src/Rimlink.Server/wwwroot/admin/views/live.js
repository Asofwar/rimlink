import { api } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { playerLink, input, refreshButton } from '../ui.js';

const FINAL = ['Ended', 'Fallback'];

export function render(root) {
  const list = h('div');
  const reason = input({ placeholder: t('player.reason') });
  root.append(
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.live')), refreshButton(load)),
    section(t('live.sessions'), h('div', { class: 'row' }, field(t('live.endReason'), reason)), list));

  async function load() {
    try {
      const sessions = await api.get('live');
      list.replaceChildren(table([
        { label: '#', key: 'id' },
        { label: t('live.kind'), key: 'kind' },
        { label: t('live.host'), render: s => playerLink(s.hostPlayerId) },
        { label: t('live.guest'), render: s => playerLink(s.guestPlayerId) },
        { label: t('raids.state'), key: 'state' },
        { label: t('live.started'), render: s => fmtDate(s.startedAt ?? s.createdAt) },
        { label: t('live.ended'), render: s => s.endedAt ? `${fmtDate(s.endedAt)} (${s.endReason ?? ''})` : '' },
        {
          label: '',
          render: s => FINAL.includes(s.state) ? null : busyButton(t('live.end'), async () => {
            if (!await confirmDialog({ title: t('live.endTitle', { id: s.id }), text: t('live.endText') })) return;
            await api.post(`live/${s.id}/end`, { reason: reason.value.trim() || null });
            banner(t('live.endedOk', { id: s.id }), 'info');
            await load();
          }, { class: 'danger' }),
        },
      ], sessions));
    } catch (error) {
      showError(error);
    }
  }

  load();
}
