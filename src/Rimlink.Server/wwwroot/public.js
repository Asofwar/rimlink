// The public server page: status, how to connect, modpack, players online, world stats. Read-only, no login.
import { h, clear, table, section } from './shared/dom.js';
import { t, loadI18n, setLang, lang } from './shared/i18n.js';

const POLL_MS = 30000;
const page = document.getElementById('page');
let last = null;
let offline = false;

async function load() {
  const response = await fetch('/api/public/status', { credentials: 'same-origin' });
  if (!response.ok) throw new Error(String(response.status));
  return response.json();
}

function stat(label, value) {
  return h('div', { class: 'stat' }, h('div', { class: 'label' }, label), h('div', { class: 'value' }, value));
}

function loginModes(s) {
  if (s.steamLogin && s.passwordLogin) return t('public.login.both');
  if (s.steamLogin) return t('public.login.steam');
  if (s.passwordLogin) return t('public.login.password');
  return t('public.login.none');
}

function fingerprintBlock(fingerprint) {
  const code = h('code', { class: 'mono' }, (fingerprint.match(/.{1,4}/g) ?? []).join(' '));
  const copy = h('button', { type: 'button' }, t('public.copy'));
  copy.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(fingerprint);
      copy.textContent = t('public.copied');
    } catch {
      window.getSelection()?.selectAllChildren(code);
    }
  });
  return h('div', null, h('div', { class: 'fingerprint' }, code, copy), h('p', { class: 'muted' }, t('public.fingerprintHint')));
}

function modLink(entry) {
  return entry.workshopId > 0
    ? h('a', { href: `https://steamcommunity.com/sharedfiles/filedetails/?id=${entry.workshopId}` }, entry.packageId)
    : entry.packageId;
}

function render(s) {
  document.title = s.serverName;
  const language = h('select', { 'aria-label': t('public.language') }, h('option', { value: 'en' }, 'English'), h('option', { value: 'ru' }, 'Русский'));
  language.value = lang();
  language.addEventListener('change', async () => {
    await setLang(language.value);
    render(s);
  });

  clear(page).append(
    h('header', { class: 'hero' },
      h('h1', null, s.serverName),
      s.description ? h('p', null, s.description) : null,
      h('div', { class: 'chips' },
        h('span', { class: 'chip' }, t('public.online', { online: s.playersOnline, max: s.maxPlayers })),
        h('span', { class: 'chip' }, t('public.version', { version: s.version, protocol: s.protocolVersion })),
        s.worldExists ? null : h('span', { class: 'chip warn' }, t('public.noWorld')),
        offline ? h('span', { class: 'chip danger' }, t('public.offline')) : null)),

    section(t('public.connect'),
      h('dl', { class: 'facts' },
        h('dt', null, t('public.address')), h('dd', { class: 'mono' }, s.address),
        h('dt', null, t('public.login')), h('dd', null, loginModes(s)),
        h('dt', null, t('public.serverPassword')), h('dd', null, s.passwordRequired ? t('common.yes') : t('common.no')),
        h('dt', null, t('public.fingerprint')), h('dd', null, fingerprintBlock(s.certificateFingerprint)))),

    section(t('public.modpack'),
      table([
        { label: '#', render: e => e.position + 1 },
        { label: t('public.mod'), render: modLink },
        { label: t('public.kind'), render: e => h('span', { class: `badge ${e.kind === 'required' ? 'warn' : ''}` }, e.kind === 'required' ? t('public.required') : t('public.allowed')) },
        { label: t('public.versionPin'), render: e => e.version || t('public.anyVersion') },
        { label: t('public.link'), render: e => e.url ? h('a', { href: e.url }, e.url) : '' },
      ], s.modpack)),

    s.players === null ? null : section(t('public.players'),
      s.players.length === 0
        ? h('p', { class: 'muted' }, t('public.noPlayers'))
        : table([{ label: t('public.player'), key: 'name' }, { label: t('public.faction'), key: 'factionName' }], s.players)),

    section(t('public.world'),
      h('div', { class: 'stats' },
        stat(t('public.settlements'), s.world.settlements),
        stat(t('public.tradesToday'), s.world.tradesToday),
        stat(t('public.raidsToday'), s.world.raidsResolvedToday),
        stat(t('public.liveNow'), s.world.liveActive),
        stat(t('public.economy'), s.world.economyFrozen ? t('public.frozen') : t('public.running')),
        stat(t('public.raids'), s.world.raidsFrozen ? t('public.frozen') : t('public.running')))),

    h('footer', { class: 'muted' }, language));
}

async function refresh() {
  try {
    last = await load();
    offline = false;
  } catch {
    offline = true;
  }
  if (last) render(last);
  else clear(page).append(h('p', { class: 'muted' }, t('public.offline')));
}

await loadI18n();
await refresh();
setInterval(refresh, POLL_MS);
