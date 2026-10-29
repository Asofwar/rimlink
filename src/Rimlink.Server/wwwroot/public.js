// The public server page: status, how to connect, modpack, players online, world stats. Read-only, no login.
import { h, clear } from './shared/dom.js';
import { t, loadI18n } from './shared/i18n.js';

const page = document.getElementById('page');

async function load() {
  const response = await fetch('/api/public/status', { credentials: 'same-origin' });
  if (!response.ok) throw new Error(String(response.status));
  return response.json();
}

await loadI18n();
try {
  const status = await load();
  document.title = status.serverName;
  clear(page).append(h('h1', null, status.serverName));
} catch {
  clear(page).append(h('p', { class: 'muted' }, t('public.offline')));
}
