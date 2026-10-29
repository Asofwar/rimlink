import { api } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate, fmtSilver } from '../../shared/format.js';
import { playerLink, stat, input, refreshButton } from '../ui.js';

export function render(root) {
  const stats = h('div', { class: 'stats' });
  const freeze = h('div');
  const lots = h('div');
  append(root, 
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.economy')), refreshButton(load)),
    stats,
    section(t('economy.freezeTitle'), freeze),
    section(t('economy.openLots'), lots));

  async function load() {
    try {
      const [s, all] = await Promise.all([api.get('economy/stats'), api.get('economy/lots')]);
      replace(stats, 
        stat(t('economy.silverInWallets'), fmtSilver(s.silverInWallets)),
        stat(t('economy.openLots'), s.openLots),
        stat(t('economy.pendingParcels'), s.pendingParcels),
        stat(t('economy.tradesToday'), s.tradesToday),
        stat(t('economy.state'), s.frozen ? t('public.frozen') : t('public.running')));
      replace(freeze, freezeForm('economy/freeze', s.frozen, s.freezeReason, load, t('economy.freezeConfirm')));
      replace(lots, table([
        { label: '#', key: 'id' },
        { label: t('economy.seller'), render: l => playerLink(l.sellerPlayerId, l.sellerName) },
        { label: t('economy.summary'), render: l => l.summary ?? '' },
        { label: t('economy.price'), render: l => fmtSilver(l.price) },
        { label: t('economy.expires'), render: l => fmtDate(l.expiresAt) },
        {
          label: '',
          render: l => busyButton(t('economy.refund'), async () => {
            if (!await confirmDialog({ title: t('economy.refundTitle', { id: l.id }), text: t('economy.refundText') })) return;
            await api.post(`economy/refund/lot/${l.id}`, {});
            banner(t('economy.refunded', { id: l.id }), 'info');
            await load();
          }),
        },
      ], all.filter(l => l.status === 'Open')));
    } catch (error) {
      showError(error);
    }
  }

  load();
}

/** Freeze or unfreeze switch shared by the economy and raid screens; confirmText says what the freeze stops. */
export function freezeForm(path, frozen, reasonNow, reload, confirmText) {
  const reason = input({ placeholder: t('player.reason') });
  return h('div', { class: 'stack' },
    h('p', null, frozen ? t('economy.frozenNow', { reason: reasonNow || '—' }) : t('economy.runningNow')),
    h('div', { class: 'row' },
      frozen ? null : field(t('player.reason'), reason),
      busyButton(frozen ? t('economy.unfreeze') : t('economy.freeze'), async () => {
        if (!frozen && !await confirmDialog({ title: t('economy.freezeTitleConfirm'), text: confirmText, confirmLabel: t('economy.freeze'), danger: false })) return;
        await api.post(path, { frozen: !frozen, reason: frozen ? null : reason.value.trim() || null });
        banner(frozen ? t('economy.unfrozen') : t('economy.frozen'), 'info');
        await reload();
      }, { class: frozen ? 'primary' : null })));
}
