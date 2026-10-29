import { api } from '../api.js';
import { h, table, busyButton, banner, showError } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate, fmtBytes } from '../../shared/format.js';
import { refreshButton } from '../ui.js';

export function render(root) {
  const list = h('div');
  root.append(
    h('div', { class: 'toolbar' },
      h('h1', { class: 'grow' }, t('nav.backups')),
      busyButton(t('backups.now'), async () => {
        const file = await api.post('backups', {});
        banner(t('backups.made', { name: file.name, size: fmtBytes(file.size) }), 'info');
        await load();
      }, { class: 'primary' }),
      refreshButton(load)),
    h('p', { class: 'muted' }, t('backups.hint')),
    list);

  async function load() {
    try {
      const files = await api.get('backups');
      list.replaceChildren(table([
        { label: t('backups.name'), key: 'name' },
        { label: t('player.size'), render: f => fmtBytes(f.size) },
        { label: t('player.created'), render: f => fmtDate(f.createdAt) },
        // A same-origin link carries the session cookie; the server checks the name and logs the download.
        { label: '', render: f => h('a', { href: `/api/admin/backups/${encodeURIComponent(f.name)}`, download: f.name }, t('backups.download')) },
      ], files));
    } catch (error) {
      showError(error);
    }
  }

  load();
}
