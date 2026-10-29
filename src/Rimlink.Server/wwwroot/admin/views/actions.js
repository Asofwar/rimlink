import { api, query } from '../api.js';
import { h, table, showError } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { actorName, refreshButton } from '../ui.js';

const PAGE = 100;

export function render(root) {
  const list = h('div');
  const more = h('button', { type: 'button' }, t('common.loadMore'));
  let entries = [];

  root.append(h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.actions')), refreshButton(() => load(true))), list, h('div', { class: 'toolbar' }, more));

  async function load(reset) {
    try {
      const before = reset || entries.length === 0 ? undefined : entries[entries.length - 1].id;
      const page = await api.get(`actions${query({ limit: PAGE, before })}`);
      entries = reset ? page : entries.concat(page);
      more.disabled = page.length < PAGE;
      list.replaceChildren(table([
        { label: t('actions.at'), render: a => fmtDate(a.at) },
        { label: t('actions.actor'), render: actorName },
        { label: t('actions.action'), key: 'action' },
        { label: t('actions.target'), render: a => a.targetName ?? (a.targetAccountId ? `#${a.targetAccountId}` : '') },
        { label: t('actions.details'), key: 'details' },
      ], entries));
    } catch (error) {
      showError(error);
    }
  }

  more.addEventListener('click', () => load(false));
  load(true);
}
