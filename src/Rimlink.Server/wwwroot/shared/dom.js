// DOM helpers for the panel and the public page. Text only ever reaches the page as text nodes: player names, chat
// lines and mod fields are untrusted, so nothing here parses HTML.
import { t } from './i18n.js';

const SAFE_HREF = /^(https:\/\/|#|\/)/;

/** Creates an element. attrs: class, id, href (https://, # or / only), on<Event> handlers, value/checked/disabled, others as attributes. */
export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs ?? {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key.startsWith('on') && typeof value === 'function') {
      el.addEventListener(key.slice(2).toLowerCase(), value);
    } else if (key === 'href') {
      const href = String(value);
      if (!SAFE_HREF.test(href)) continue;
      el.setAttribute('href', href);
      if (href.startsWith('https://')) {
        el.setAttribute('rel', 'noopener noreferrer');
        el.setAttribute('target', '_blank');
      }
    } else if (key === 'class') {
      el.className = value;
    } else if (key === 'value') {
      el.value = value;
    } else if (key === 'checked' || key === 'disabled') {
      el[key] = Boolean(value);
    } else {
      el.setAttribute(key, value === true ? '' : String(value));
    }
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of [children].flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

/** A table in a horizontally scrolling wrapper. columns: [{ key, label, render?(row) }]. */
export function table(columns, rows, emptyText) {
  const head = h('thead', null, h('tr', null, columns.map(c => h('th', null, c.label))));
  const body = h('tbody');
  if (rows.length === 0) {
    body.append(h('tr', null, h('td', { class: 'empty', colspan: columns.length }, emptyText ?? t('common.empty'))));
  }
  for (const row of rows) {
    body.append(h('tr', null, columns.map(c => h('td', null, c.render ? c.render(row) : row[c.key] ?? ''))));
  }
  return h('div', { class: 'table-wrap' }, h('table', null, head, body));
}

/** A button that stays disabled while its action runs, so a double click sends one request. Errors become a banner. */
export function busyButton(label, action, attrs) {
  const button = h('button', { type: 'button', ...attrs }, label);
  button.addEventListener('click', async () => {
    if (button.disabled) return;
    button.disabled = true;
    try {
      await action();
    } catch (error) {
      showError(error);
    } finally {
      button.disabled = false;
    }
  });
  return button;
}

/** A labelled form control. */
export function field(label, control) {
  return h('label', { class: 'field' }, h('span', null, label), control);
}

/** A titled block of a page. */
export function section(title, ...children) {
  return h('section', { class: 'card' }, h('h2', null, title), ...children);
}

export function banner(message, kind = 'error') {
  const host = document.getElementById('banners');
  if (!host) return;
  const close = h('button', { type: 'button', class: 'link', 'aria-label': t('common.close') }, '×');
  const item = h('div', { class: `banner ${kind}`, role: kind === 'error' ? 'alert' : 'status' }, h('span', null, message), close);
  close.addEventListener('click', () => item.remove());
  host.append(item);
  if (kind !== 'error') setTimeout(() => item.remove(), 5000);
}

/** Shows an API or network error as a banner. */
export function showError(error) {
  if (error?.status === 0) banner(t('common.error.network'));
  else if (error?.status === 401) return;
  else banner(error?.detail || t('common.error.status', { status: error?.status ?? '?' }));
}

/** A modal confirmation; with typeToConfirm the OK button stays disabled until the input equals it. Resolves to true or false. */
export function confirmDialog({ title, text, typeToConfirm, confirmLabel, danger = true }) {
  return new Promise(resolve => {
    const ok = h('button', { type: 'submit', class: danger ? 'danger' : 'primary', disabled: Boolean(typeToConfirm) }, confirmLabel ?? t('common.confirm'));
    const cancel = h('button', { type: 'button' }, t('common.cancel'));
    const input = typeToConfirm ? h('input', { type: 'text', autocomplete: 'off', 'aria-label': t('common.typeToConfirm', { text: typeToConfirm }) }) : null;
    input?.addEventListener('input', () => { ok.disabled = input.value !== typeToConfirm; });
    const form = h('form', { method: 'dialog' },
      h('h2', null, title),
      text ? h('p', null, text) : null,
      typeToConfirm ? h('p', { class: 'muted' }, t('common.typeToConfirm', { text: typeToConfirm })) : null,
      input,
      h('div', { class: 'actions' }, cancel, ok));
    const dialog = h('dialog', { class: 'confirm' }, form);
    let result = false;
    form.addEventListener('submit', event => {
      if (ok.disabled) { event.preventDefault(); return; }
      result = true;
    });
    cancel.addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => { dialog.remove(); resolve(result); });
    document.body.append(dialog);
    dialog.showModal();
    (input ?? cancel).focus();
  });
}
