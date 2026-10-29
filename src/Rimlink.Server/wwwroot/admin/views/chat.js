import { api, query } from '../api.js';
import { h, table, busyButton, field, banner, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { playerLink, numberInput, refreshButton } from '../ui.js';

const PAGE = 100;

/** The public channel, newest first, with a player filter and paging back. Direct messages are never shown. */
export function render(root) {
  const player = numberInput({ min: 1, placeholder: '#id' });
  const list = h('div');
  const more = h('button', { type: 'button' }, t('common.loadMore'));
  let lines = [];

  append(root, 
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.chat')), field(t('chat.player'), player), refreshButton(() => load(true))),
    list,
    h('div', { class: 'toolbar' }, more));

  function author(line) {
    if (line.fromPlayerId) return playerLink(line.fromPlayerId, line.fromName);
    if (line.author) return t('chat.external', { author: line.author });
    return t('chat.system');
  }

  function draw() {
    replace(list, table([
      { label: t('chat.at'), render: l => fmtDate(l.at) },
      { label: t('chat.from'), render: author },
      { label: t('chat.text'), render: l => h('span', { class: 'chat-line' }, l.text) },
      {
        label: '',
        render: l => l.fromPlayerId ? busyButton(t('overview.mute30'), async () => {
          await api.post(`players/${l.fromPlayerId}/mute`, { minutes: 30 });
          banner(t('player.muted', { name: l.fromName ?? `#${l.fromPlayerId}` }), 'info');
        }) : null,
      },
    ], lines));
  }

  async function load(reset) {
    try {
      const before = reset || lines.length === 0 ? undefined : lines[lines.length - 1].id;
      const page = await api.get(`chat${query({ playerId: player.value || undefined, before, limit: PAGE })}`);
      lines = reset ? page : lines.concat(page);
      more.disabled = page.length < PAGE;
      draw();
    } catch (error) {
      showError(error);
    }
  }

  more.addEventListener('click', () => load(false));
  player.addEventListener('change', () => load(true));
  load(true);
}
