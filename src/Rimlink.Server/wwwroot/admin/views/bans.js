import { api } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate } from '../../shared/format.js';
import { input, numberInput, refreshButton, valid } from '../ui.js';

export function render(root) {
  const list = h('div');
  const kind = h('select', null,
    h('option', { value: 'playerId' }, t('bans.byPlayer')),
    h('option', { value: 'steamId' }, t('bans.bySteam')),
    h('option', { value: 'ip' }, t('bans.byIp')));
  const target = input({ placeholder: t('bans.target'), required: true });
  const days = numberInput({ min: 1, step: 'any', placeholder: t('player.banForever') });
  const reason = input({ placeholder: t('player.reasonOptional') });

  append(root, 
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.bans')), refreshButton(load)),
    section(t('bans.new'), h('div', { class: 'row' },
      field(t('bans.kind'), kind),
      field(t('bans.target'), target),
      field(t('player.banDays'), days),
      field(t('player.reason'), reason),
      busyButton(t('player.ban'), async () => {
        if (!valid(target, days)) return;
        const value = target.value.trim();
        const body = { reason: reason.value.trim() || t('player.banDefaultReason'), days: days.value === '' ? null : Number(days.value) };
        // SteamIDs travel as strings: 17 digits exceed JavaScript's safe integers.
        body[kind.value] = kind.value === 'playerId' ? Number(value.replace(/^#/, '')) : value;
        const text = `${body.days ? t('player.banDaysText', { days: body.days }) : t('player.banForeverText')} ${t('player.banReasonText', { reason: body.reason })}`;
        if (!await confirmDialog({ title: t('bans.confirm', { target: value }), text, confirmLabel: t('player.ban') })) return;
        await api.post('bans', body);
        banner(t('bans.added'), 'info');
        target.value = '';
        await load();
      }, { class: 'danger' }))),
    section(t('bans.active'), list));

  async function load() {
    try {
      const bans = await api.get('bans');
      replace(list, table([
        { label: '#', key: 'id' },
        { label: t('player.name'), render: b => b.playerName ?? '' },
        { label: t('player.steamId'), render: b => b.steamIdText ?? '' },
        { label: t('player.ip'), render: b => b.ip ?? '' },
        { label: t('player.reason'), key: 'reason' },
        { label: t('bans.expires'), render: b => b.expiresAt ? fmtDate(b.expiresAt) : t('bans.permanent') },
        { label: t('player.created'), render: b => fmtDate(b.createdAt) },
        {
          label: '',
          render: b => busyButton(t('bans.pardon'), async () => {
            if (!await confirmDialog({ title: t('bans.pardonTitle', { id: b.id }), danger: false, confirmLabel: t('bans.pardon') })) return;
            await api.del(`bans/${b.id}`);
            banner(t('bans.pardoned', { id: b.id }), 'info');
            await load();
          }),
        },
      ], bans));
    } catch (error) {
      showError(error);
    }
  }

  load();
}
