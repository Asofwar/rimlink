// Small building blocks shared by the admin screens.
import { h, showError, replace } from '../shared/dom.js';
import { t } from '../shared/i18n.js';

const ROLES = ['player', 'moderator', 'admin'];

/** Role from either a name ("moderator") or the enum number some older routes return. */
export function roleName(role) {
  return typeof role === 'number' ? ROLES[role] ?? String(role) : role;
}

export function playerLink(id, name) {
  return id ? h('a', { href: `#/players/${id}` }, name || `#${id}`) : '—';
}

export function badge(text, kind = '') {
  return h('span', { class: `badge ${kind}` }, text);
}

export function stat(label, value) {
  return h('div', { class: 'stat' }, h('div', { class: 'label' }, label), h('div', { class: 'value' }, value ?? '—'));
}

export function actorName(action) {
  return action.actorName ?? (action.actorAccountId === 0 ? 'api' : `#${action.actorAccountId}`);
}

/** Runs refresh now and every ms; an error shows one banner until a refresh succeeds again. Returns the stop function. */
export function poll(refresh, ms) {
  let failing = false;
  const run = async () => {
    try {
      await refresh();
      failing = false;
    } catch (error) {
      if (!failing) showError(error);
      failing = true;
    }
  };
  run();
  const timer = setInterval(run, ms);
  return () => clearInterval(timer);
}

/** Loads one block of a page; a failure shows the error inside the block instead of breaking the page. */
export async function fill(container, load) {
  try {
    replace(container, await load());
  } catch (error) {
    replace(container, h('p', { class: 'muted' }, error?.detail || t('common.error.status', { status: error?.status ?? '?' })));
    if (error?.status === 0) showError(error);
  }
}

export function input(attrs) {
  return h('input', { type: 'text', ...attrs });
}

export function numberInput(attrs) {
  return h('input', { type: 'number', ...attrs });
}

/** A refresh button for a screen's toolbar. */
export function refreshButton(action) {
  const button = h('button', { type: 'button' }, t('common.refresh'));
  button.addEventListener('click', async () => {
    button.disabled = true;
    try { await action(); } finally { button.disabled = false; }
  });
  return button;
}
