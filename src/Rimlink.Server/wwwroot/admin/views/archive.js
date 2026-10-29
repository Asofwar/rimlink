import { api, query } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate, fmtSilver } from '../../shared/format.js';
import { playerLink, badge, refreshButton } from '../ui.js';

/** Refusals the archive routes answer with (problem details), shown in the panel's language. */
const REFUSALS = {
  'tile taken': 'archive.refused.tileTaken',
  'player has a colony': 'archive.refused.hasColony',
  'already restored': 'archive.refused.restored',
  'archive damaged': 'archive.refused.damaged',
  'unknown archive': 'archive.refused.unknown',
  'no colony': 'archive.refused.noColony',
  'player is online': 'archive.refused.online',
};

/** Runs an archive action; a known refusal becomes a translated banner instead of the raw server text. */
export async function withRefusals(action) {
  try {
    await action();
  } catch (error) {
    const key = REFUSALS[error?.detail];
    if (key && (error.status === 409 || error.status === 404)) banner(t(key), 'error');
    else throw error;
  }
}

/** Colonies the sweep or an admin moved out of the world, with their restore. */
export function render(root) {
  const list = h('div');
  const state = h('select', null, ['archived', 'restored', 'all'].map(s => h('option', { value: s }, t(`archive.state.${s}`))));
  state.addEventListener('change', load);

  append(root,
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.archive')), refreshButton(load)),
    h('p', { class: 'facts-line' }, t('archive.hint')),
    section(t('archive.colonies'), h('div', { class: 'stack' }, h('div', { class: 'row' }, field(t('archive.show'), state)), list)));

  async function load() {
    try {
      const rows = await api.get(`archive${query({ state: state.value })}`);
      replace(list, table([
        { label: '#', key: 'id' },
        { label: t('player.name'), render: r => playerLink(r.playerId, r.playerName) },
        { label: t('archive.colony'), key: 'colony' },
        { label: t('archive.tile'), key: 'tile' },
        { label: t('archive.archivedAt'), render: r => fmtDate(r.archivedAt) },
        { label: t('archive.reason'), render: r => r.reason === 'auto' ? t('archive.reasonAuto') : t('archive.reasonAdmin', { actor: r.archivedBy }) },
        { label: t('player.wealth'), render: r => fmtSilver(r.wealthTotal) },
        { label: t('archive.age'), render: r => r.colonyGameDays == null ? '—' : t('archive.ageValue', { days: r.colonyGameDays }) },
        {
          label: t('archive.stateLabel'),
          render: r => r.state === 'archived'
            ? badge(t('archive.state.archived'), 'warn')
            : badge(t('archive.restoredValue', { at: fmtDate(r.restoredAt), by: r.restoredBy === 'player' ? t('archive.byPlayer') : r.restoredBy })),
        },
        {
          label: '',
          render: r => r.state !== 'archived' ? null : busyButton(t('archive.restore'), () => withRefusals(async () => {
            if (!await confirmDialog({
              title: t('archive.restoreTitle', { colony: r.colony, name: r.playerName ?? `#${r.playerId}` }),
              text: t('archive.restoreText', { tile: r.tile }),
              danger: false,
              confirmLabel: t('archive.restore'),
            })) return;
            await api.post(`archive/${r.id}/restore`, {});
            banner(t('archive.restored', { colony: r.colony }), 'info');
            await load();
          })),
        },
      ], rows));
    } catch (error) {
      showError(error);
    }
  }

  load();
}
