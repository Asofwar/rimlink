import { api, query } from '../api.js';
import { h, table, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { playerLink, badge, input } from '../ui.js';

const PAGE = 50;

export function render(root) {
  const search = input({ placeholder: t('players.search'), 'aria-label': t('players.search'), class: 'grow' });
  const results = h('div');
  const pager = h('div', { class: 'toolbar' });
  let offset = 0;
  let timer = null;

  append(root, h('h1', null, t('nav.players')), h('div', { class: 'toolbar' }, search), results, pager);

  async function load() {
    try {
      const page = await api.get(`players${query({ q: search.value.trim(), limit: PAGE, offset })}`);
      replace(results, table([
        { label: t('player.name'), render: p => playerLink(p.id, p.name) },
        { label: t('player.faction'), key: 'factionName' },
        { label: t('player.role'), key: 'role' },
        {
          label: t('players.state'),
          render: p => [
            p.online ? badge(t('players.online'), 'on') : null,
            p.banned ? badge(t('players.banned'), 'bad') : null,
            p.mutedUntil && new Date(p.mutedUntil) > new Date() ? badge(t('players.muted'), 'warn') : null,
            p.peaceful ? badge(t('players.peaceful')) : null,
          ],
        },
        { label: t('player.lastSeen'), render: p => fmtDate(p.lastSeenAt) },
      ], page.items));

      const prev = h('button', { type: 'button', disabled: offset === 0 }, '←');
      const next = h('button', { type: 'button', disabled: offset + PAGE >= page.total }, '→');
      prev.addEventListener('click', () => { offset = Math.max(0, offset - PAGE); load(); });
      next.addEventListener('click', () => { offset += PAGE; load(); });
      replace(pager, prev,
        h('span', { class: 'muted' }, t('players.range', { from: page.total === 0 ? 0 : offset + 1, to: Math.min(offset + PAGE, page.total), total: page.total })),
        next);
    } catch (error) {
      showError(error);
    }
  }

  search.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(() => { offset = 0; load(); }, 300);
  });
  load();
  search.focus();
  return () => clearTimeout(timer);
}
