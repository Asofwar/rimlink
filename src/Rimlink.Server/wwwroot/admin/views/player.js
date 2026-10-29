import { api, query } from '../api.js';
import { h, table, busyButton, section, field, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { fmtDate, fmtBytes, fmtSilver } from '../../shared/format.js';
import { playerLink, badge, fill, input, numberInput } from '../ui.js';

/** The player card: facts, moderation and admin actions, saves, economy, shop, raids and live sessions, chat lines. */
export function render(root, params) {
  const id = Number(params.id);
  const facts = h('div');
  const actions = h('div');
  const saves = h('div');
  const economy = h('div');
  const shop = h('div');
  const raids = h('div');
  const chat = h('div');
  append(root, 
    h('h1', null, t('player.title', { id })),
    section(t('player.facts'), facts),
    section(t('player.actions'), actions),
    section(t('player.saves'), saves),
    section(t('player.economy'), economy),
    section(t('player.shop'), shop),
    section(t('player.raidsLive'), raids),
    section(t('player.chat'), chat));

  async function done(message) {
    banner(message, 'info');
    await load();
  }

  async function load() {
    let card;
    try {
      card = await api.get(`players/${id}`);
    } catch (error) {
      showError(error);
      return;
    }
    root.querySelector('h1').textContent = card.name;
    replace(facts, factsList(card));
    replace(actions, actionsBlock(card, done));
    fill(saves, () => savesBlock(card, done));
    fill(economy, () => economyBlock(card, done));
    fill(shop, () => shopBlock(id));
    fill(raids, () => raidsBlock(id));
    fill(chat, () => chatBlock(id));
  }

  load();
}

function factsList(card) {
  const rows = [
    [t('player.name'), card.name],
    [t('player.faction'), card.factionName],
    [t('player.role'), card.role],
    [t('player.state'), [
      card.online ? badge(t('players.online'), 'on') : badge(t('players.offline')),
      card.banned ? badge(t('players.banned'), 'bad') : null,
      card.mutedUntil && new Date(card.mutedUntil) > new Date() ? badge(t('player.mutedUntil', { at: fmtDate(card.mutedUntil) }), 'warn') : null,
      card.peaceful ? badge(t('players.peaceful')) : null,
      card.visitsClosed ? badge(t('player.visitsClosed')) : null,
    ]],
    [t('player.steamId'), card.steamId ?? '—'],
    [t('player.login'), card.login ?? '—'],
    [t('player.session'), card.session ? t('player.sessionValue', { ip: card.session.ip, since: fmtDate(card.session.since) }) : '—'],
    [t('player.lastIp'), card.lastIp ?? '—'],
    [t('player.lastSeen'), fmtDate(card.lastSeenAt)],
    [t('player.created'), fmtDate(card.createdAt)],
    [t('player.settlements'), card.settlements.length ? card.settlements.map(s => `${s.name} (${s.tile})`).join(', ') : '—'],
    [t('player.wealth'), fmtSilver(card.wealthTotal)],
    [t('player.currentSave'), card.currentSave ? t('player.saveValue', { version: card.currentSave.version, size: fmtBytes(card.currentSave.size), at: fmtDate(card.currentSave.createdAt) }) : '—'],
    [t('player.activeBans'), card.activeBans.length ? card.activeBans.map(b => `#${b.id} ${b.reason} (${b.expiresAt ? fmtDate(b.expiresAt) : t('bans.permanent')})`).join('; ') : '—'],
  ];
  return h('dl', { class: 'facts' }, rows.map(([label, value]) => [h('dt', null, label), h('dd', null, value)]));
}

function actionsBlock(card, done) {
  const id = card.id;
  const kickReason = input({ placeholder: t('player.reason') });
  const muteMinutes = numberInput({ min: 1, value: 30 });
  const banDays = numberInput({ min: 0, step: 'any', placeholder: t('player.banForever') });
  const banReason = input({ placeholder: t('player.reason') });
  const role = h('select', null, ['player', 'moderator', 'admin'].map(r => h('option', { value: r }, r)));
  role.value = card.role;
  const newName = input({ value: card.name });
  const newFaction = input({ value: card.factionName });

  return h('div', { class: 'stack' },
    h('div', { class: 'row' },
      field(t('player.reason'), kickReason),
      busyButton(t('player.kick'), async () => {
        await api.post(`players/${id}/kick`, { reason: kickReason.value.trim() || null });
        await done(t('player.kicked', { name: card.name }));
      })),
    h('div', { class: 'row' },
      field(t('player.muteMinutes'), muteMinutes),
      busyButton(t('player.mute'), async () => {
        await api.post(`players/${id}/mute`, { minutes: Number(muteMinutes.value) || 30 });
        await done(t('player.muted', { name: card.name }));
      }),
      busyButton(t('player.unmute'), async () => {
        await api.del(`players/${id}/mute`);
        await done(t('player.unmuted', { name: card.name }));
      })),
    h('div', { class: 'row' },
      field(t('player.banDays'), banDays),
      field(t('player.reason'), banReason),
      busyButton(t('player.ban'), async () => {
        const days = banDays.value === '' ? null : Number(banDays.value);
        if (!await confirmDialog({ title: t('player.banTitle', { name: card.name }), text: days ? t('player.banDaysText', { days }) : t('player.banForeverText') })) return;
        await api.post('bans', { playerId: id, reason: banReason.value.trim() || t('player.banDefaultReason'), days });
        await done(t('player.banned', { name: card.name }));
      }, { class: 'danger' })),
    h('div', { class: 'row' },
      field(t('player.role'), role),
      busyButton(t('player.setRole'), async () => {
        await api.post(`players/${id}/role`, { role: role.value });
        await done(t('player.roleSet', { name: card.name, role: role.value }));
      })),
    h('div', { class: 'row' },
      field(t('player.name'), newName),
      field(t('player.faction'), newFaction),
      busyButton(t('player.rename'), async () => {
        await api.post(`players/${id}/rename`, { name: newName.value.trim(), factionName: newFaction.value.trim() || null });
        await done(t('player.renamed', { name: newName.value.trim() }));
      })),
    h('div', { class: 'row' },
      busyButton(card.peaceful ? t('player.peacefulOff') : t('player.peacefulOn'), async () => {
        await api.post(`players/${id}/peaceful`, { peaceful: !card.peaceful });
        await done(t('player.peacefulSet'));
      }),
      busyButton(t('player.reset'), async () => {
        if (!await confirmDialog({ title: t('player.resetTitle', { name: card.name }), text: t('player.resetText'), typeToConfirm: card.name })) return;
        await api.post(`players/${id}/reset`, {});
        await done(t('player.resetDone', { name: card.name }));
      }, { class: 'danger' })));
}

async function savesBlock(card, done) {
  const versions = await api.get(`players/${card.id}/saves`);
  return table([
    { label: t('player.version'), render: s => [String(s.version), s.isCurrent ? badge(t('player.current'), 'on') : null] },
    { label: t('player.size'), render: s => fmtBytes(s.size) },
    { label: t('player.tick'), key: 'gameTick' },
    { label: t('player.created'), render: s => fmtDate(s.createdAt) },
    {
      label: '',
      render: s => s.isCurrent ? null : busyButton(t('player.rollback'), async () => {
        if (!await confirmDialog({ title: t('player.rollbackTitle', { version: s.version }), text: t('player.rollbackText') })) return;
        await api.post(`players/${card.id}/saves/${s.version}/rollback`, {});
        await done(t('player.rolledBack', { version: s.version }));
      }, { class: 'danger' }),
    },
  ], versions);
}

async function economyBlock(card, done) {
  const [parcels, lots] = await Promise.all([api.get(`economy/parcels/${card.id}`), api.get(`economy/lots${query({ playerId: card.id })}`)]);
  const delta = numberInput({ step: 1, placeholder: '+100 / -100' });
  const reason = input({ placeholder: t('player.reason') });
  const refund = (kind, itemId) => busyButton(t('economy.refund'), async () => {
    if (!await confirmDialog({ title: t('economy.refundTitle', { id: itemId }), text: t('economy.refundText') })) return;
    await api.post(`economy/refund/${kind}/${itemId}`, {});
    await done(t('economy.refunded', { id: itemId }));
  });

  return h('div', { class: 'stack' },
    h('p', null, t('player.balance', { balance: fmtSilver(card.walletBalance) })),
    h('div', { class: 'row' },
      field(t('player.delta'), delta),
      field(t('player.reason'), reason),
      busyButton(t('player.adjust'), async () => {
        const value = Number(delta.value);
        if (!Number.isInteger(value) || value === 0) return;
        const result = await api.post(`economy/wallet/${card.id}`, { delta: value, reason: reason.value.trim() || null });
        await done(t('player.balanceNow', { balance: fmtSilver(result.balance) }));
      })),
    h('h3', null, t('economy.parcels')),
    table([
      { label: '#', key: 'id' },
      { label: t('economy.kind'), key: 'kind' },
      { label: t('economy.from'), render: p => playerLink(p.fromPlayerId) },
      { label: t('economy.silver'), render: p => fmtSilver(p.silver) },
      { label: t('player.created'), render: p => fmtDate(p.createdAt) },
      { label: t('economy.delivered'), render: p => p.deliveredAt ? fmtDate(p.deliveredAt) : t('economy.pending') },
      { label: '', render: p => p.deliveredAt ? null : refund('parcel', p.id) },
    ], parcels),
    h('h3', null, t('economy.lots')),
    table([
      { label: '#', key: 'id' },
      { label: t('economy.summary'), render: l => l.summary ?? '' },
      { label: t('economy.price'), render: l => fmtSilver(l.price) },
      { label: t('economy.status'), key: 'status' },
      { label: t('economy.expires'), render: l => fmtDate(l.expiresAt) },
      { label: '', render: l => l.status === 'Open' ? refund('lot', l.id) : null },
    ], lots));
}

async function shopBlock(id) {
  const [items, removals] = await Promise.all([api.get(`shop/${id}`), api.get(`shop/${id}/removals`)]);
  return h('div', { class: 'stack' },
    table([
      { label: t('shop.item'), render: s => s.label || s.defName },
      { label: t('shop.count'), key: 'count' },
      { label: t('economy.price'), render: s => fmtSilver(s.price) },
      { label: t('shop.sold'), render: s => s.soldAt ? fmtDate(s.soldAt) : '' },
    ], items),
    h('h3', null, t('shop.removals')),
    table([
      { label: '#', key: 'id' },
      { label: t('shop.thing'), key: 'thingId' },
      { label: t('shop.count'), key: 'count' },
      { label: t('player.created'), render: r => fmtDate(r.createdAt) },
    ], removals));
}

async function raidsBlock(id) {
  const [raids, live] = await Promise.all([api.get(`raids${query({ playerId: id })}`), api.get(`live${query({ playerId: id })}`)]);
  return h('div', { class: 'stack' },
    table([
      { label: '#', key: 'id' },
      { label: t('raids.attacker'), render: r => playerLink(r.attackerPlayerId) },
      { label: t('raids.target'), render: r => playerLink(r.targetPlayerId) },
      { label: t('raids.state'), key: 'state' },
      { label: t('player.created'), render: r => fmtDate(r.createdAt) },
    ], raids),
    h('h3', null, t('nav.live')),
    table([
      { label: '#', key: 'id' },
      { label: t('live.kind'), key: 'kind' },
      { label: t('live.host'), render: s => playerLink(s.hostPlayerId) },
      { label: t('live.guest'), render: s => playerLink(s.guestPlayerId) },
      { label: t('raids.state'), key: 'state' },
      { label: t('player.created'), render: s => fmtDate(s.createdAt) },
    ], live));
}

async function chatBlock(id) {
  const lines = await api.get(`chat${query({ playerId: id, limit: 50 })}`);
  return table([
    { label: t('chat.at'), render: l => fmtDate(l.at) },
    { label: t('chat.text'), render: l => h('span', { class: 'chat-line' }, l.text) },
  ], lines);
}
