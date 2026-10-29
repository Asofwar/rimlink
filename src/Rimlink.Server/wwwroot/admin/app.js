// The admin panel shell: login, header, navigation and a hash router (#/players/12). Each screen is a module in
// views/ exporting render(root, params) and optionally returning a function that stops its polling.
import { api, resetUnauthorized } from './api.js';
import { h, clear, clearBanners, showError, append, replace } from '../shared/dom.js';
import { t, loadI18n, setLang, lang } from '../shared/i18n.js';
import * as login from './views/login.js';

const ROUTES = [
  ['', 'overview'],
  ['players', 'players'],
  ['players/:id', 'player'],
  ['archive', 'archive'],
  ['bans', 'bans'],
  ['economy', 'economy'],
  ['raids', 'raids'],
  ['live', 'live'],
  ['chat', 'chat'],
  ['modpack', 'modpack'],
  ['backups', 'backups'],
  ['server', 'server'],
  ['actions', 'actions'],
];
const NAV = ['', 'players', 'archive', 'bans', 'economy', 'raids', 'live', 'chat', 'modpack', 'backups', 'server', 'actions'];
const HEADER_POLL_MS = 10000;

const app = document.getElementById('app');
let stopView = null;
let headerTimer = null;
let serverName = 'Rimlink';

function match(path) {
  const parts = path.split('/').filter(Boolean);
  for (const [pattern, view] of ROUTES) {
    const want = pattern.split('/').filter(Boolean);
    if (want.length !== parts.length) continue;
    const params = {};
    let ok;
    try {
      ok = want.every((segment, i) => segment.startsWith(':') ? (params[segment.slice(1)] = decodeURIComponent(parts[i]), true) : segment === parts[i]);
    } catch {
      ok = false; // a malformed %-escape in the hash: fall through to the overview
    }
    if (ok) return { view, params };
  }
  return { view: 'overview', params: {} };
}

function currentPath() {
  return location.hash.replace(/^#\/?/, '');
}

async function showRoute() {
  const main = document.getElementById('view');
  if (!main) return;
  stopView?.();
  stopView = null;
  clear(main);
  clearBanners();
  const { view, params } = match(currentPath());
  for (const link of document.querySelectorAll('nav.sidebar a')) {
    const active = link.dataset.route === (view === 'overview' ? '' : view === 'player' ? 'players' : view);
    link.classList.toggle('active', active);
    if (active) {
      link.setAttribute('aria-current', 'page');
      // On a phone the nav is a scrolling strip: keep the current screen's pill in view.
      link.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    } else {
      link.removeAttribute('aria-current');
    }
  }
  let module;
  try {
    module = await import(`./views/${view}.js`);
  } catch {
    append(main, h('p', { class: 'muted' }, t('common.soon')));
    return;
  }
  try {
    stopView = (await module.render(main, params)) ?? null;
  } catch (error) {
    showError(error);
  }
}

async function refreshHeader(chips) {
  try {
    const status = await api.get('status');
    replace(chips, 
      h('span', { class: 'chip' }, t('header.online', { online: status.playersOnline, max: status.maxPlayers })),
      status.economy?.frozen ? h('span', { class: 'chip warn', title: status.economy.reason ?? '' }, t('header.economyFrozen')) : null,
      status.raids?.frozen ? h('span', { class: 'chip warn', title: status.raids.reason ?? '' }, t('header.raidsFrozen')) : null);
  } catch (error) {
    if (error.status !== 401) replace(chips, h('span', { class: 'chip danger' }, t('common.error.network')));
  }
}

async function showShell() {
  clear(app);
  try {
    serverName = (await api.get('config')).panel?.serverName?.trim() || 'Rimlink';
  } catch (error) {
    // A 401 here already showed the login screen; drawing the shell over it would leave a "logged in" header.
    if (error.status === 401) return;
    serverName = 'Rimlink';
  }
  document.title = t('app.title', { name: serverName });

  const chips = h('div', { class: 'chips' });
  const language = h('select', { 'aria-label': t('header.language') },
    h('option', { value: 'en' }, 'English'),
    h('option', { value: 'ru' }, 'Русский'));
  language.value = lang();
  language.addEventListener('change', async () => {
    await setLang(language.value);
    await showShell();
  });
  const logout = h('button', { type: 'button', class: 'link' }, t('header.logout'));
  logout.addEventListener('click', async () => {
    try {
      await api.panel.logout();
    } finally {
      showLogin();
    }
  });

  const nav = h('nav', { class: 'sidebar', 'aria-label': t('nav.label') }, NAV.map(route => h('a', { href: `#/${route}`, 'data-route': route }, t(`nav.${route || 'overview'}`))));
  append(app, 
    h('header', { class: 'topbar' }, h('strong', { class: 'brand' }, serverName), chips, h('div', { class: 'spacer' }), language, logout),
    h('div', { class: 'layout' }, nav, h('main', { id: 'view' })));

  clearInterval(headerTimer);
  refreshHeader(chips);
  headerTimer = setInterval(() => refreshHeader(chips), HEADER_POLL_MS);
  await showRoute();
}

function showLogin() {
  stopView?.();
  stopView = null;
  clearInterval(headerTimer);
  clear(app);
  document.title = t('login.title');
  login.render(app, () => {
    resetUnauthorized();
    clearBanners();
    showShell();
  });
}

window.addEventListener('hashchange', showRoute);
window.addEventListener('rimlink:unauthorized', showLogin);

await loadI18n();
try {
  await api.panel.session();
  resetUnauthorized();
  await showShell();
} catch (error) {
  // A 401 already raised rimlink:unauthorized, which shows the login.
  if (error.status !== 401) {
    showError(error);
    showLogin();
  }
}
