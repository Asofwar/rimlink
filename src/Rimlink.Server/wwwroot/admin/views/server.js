import { api } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { input, numberInput, valid } from '../ui.js';

export function render(root) {
  const text = h('textarea', { 'aria-label': t('server.broadcastText'), maxlength: 500, required: true });
  const seconds = numberInput({ min: 0, value: 60 });
  const reason = input({ placeholder: t('player.reason') });
  const config = h('div');
  let serverName = '';

  append(root, 
    h('h1', null, t('nav.server')),
    section(t('server.broadcast'),
      field(t('server.broadcastText'), text),
      h('div', { class: 'actions' },
        busyButton(t('server.send'), async () => {
          text.value = text.value.trim();
          if (!valid(text)) return;
          const message = text.value;
          await api.post('broadcast', { text: message });
          text.value = '';
          banner(t('server.sent'), 'info');
        }, { class: 'primary' }))),
    section(t('server.shutdown'),
      h('p', { class: 'muted' }, t('server.saveAllHint')),
      h('div', { class: 'row' },
        busyButton(t('server.saveAll'), async () => {
          await api.post('saveall', {});
          banner(t('server.saveAllSent'), 'info');
        })),
      h('div', { class: 'row' },
        field(t('server.seconds'), seconds),
        field(t('player.reason'), reason),
        busyButton(t('server.shutdownNow'), async () => {
          const delay = Math.max(0, Number(seconds.value) || 0);
          if (!await confirmDialog({ title: t('server.shutdownTitle', { seconds: delay }), text: t('server.shutdownText'), confirmLabel: t('server.shutdownNow') })) return;
          await api.post('shutdown', { inSeconds: delay, reason: reason.value.trim() || null });
          banner(t('server.shuttingDown', { seconds: delay }), 'info');
        }, { class: 'danger' }))),
    section(t('server.recreate'),
      h('p', { class: 'muted' }, t('server.recreateText')),
      h('div', { class: 'actions' },
        busyButton(t('server.recreateNow'), async () => {
          const word = serverName || 'RECREATE';
          if (!await confirmDialog({ title: t('server.recreateTitle'), text: t('server.recreateText'), typeToConfirm: word, confirmLabel: t('server.recreateNow') })) return;
          await api.post('world/recreate', {});
          banner(t('server.recreated'), 'info');
        }, { class: 'danger' }))),
    h('details', { class: 'card config' }, h('summary', null, t('server.config')), config));

  async function load() {
    try {
      const values = await api.get('config');
      serverName = values.panel?.serverName?.trim() ?? '';
      replace(config, ...Object.entries(values).map(([name, settings]) => h('div', null,
        h('h3', null, name),
        table([
          { label: t('server.key'), key: 'key' },
          { label: t('server.value'), render: row => h('span', { class: 'mono' }, row.value) },
        ], Object.entries(settings ?? {}).map(([key, value]) => ({ key, value: typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value) }))))));
    } catch (error) {
      showError(error);
    }
  }

  load();
}
