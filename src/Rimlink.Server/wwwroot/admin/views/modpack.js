import { api } from '../api.js';
import { h, busyButton, banner, confirmDialog, showError, append, replace } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';
import { refreshButton } from '../ui.js';

/** The modpack editor: order, package id, pinned version, Workshop id, link, required/allowed. Defs stay with the CLI. */
export function render(root) {
  const body = h('div');
  let original = [];
  let rows = [];
  let snapshot = '[]';

  append(root, 
    h('div', { class: 'toolbar' }, h('h1', { class: 'grow' }, t('nav.modpack')), refreshButton(async () => {
      if (dirty() && !await confirmDialog({ title: t('modpack.discardTitle'), text: t('modpack.discardText'), confirmLabel: t('modpack.discard') })) return;
      await load();
    })),
    h('p', { class: 'muted' }, t('modpack.hint')),
    body);

  function cell(row, key, attrs) {
    const control = h('input', { type: 'text', value: row[key] ?? '', ...attrs });
    control.addEventListener('input', () => { row[key] = control.value; });
    return control;
  }

  function move(index, by) {
    const target = index + by;
    if (target < 0 || target >= rows.length) return;
    [rows[index], rows[target]] = [rows[target], rows[index]];
    draw();
    // Keep keyboard focus on the row that moved.
    body.querySelectorAll('tbody tr')[target]?.querySelector(by < 0 ? '[data-move="up"]' : '[data-move="down"]')?.focus();
  }

  function dirty() {
    return JSON.stringify(rows) !== snapshot;
  }

  function draw() {
    const kindSelect = row => {
      const select = h('select', { 'aria-label': t('public.kind') },
        h('option', { value: 'Required' }, t('public.required')),
        h('option', { value: 'Allowed' }, t('public.allowed')));
      select.value = row.kind;
      select.addEventListener('change', () => { row.kind = select.value; });
      return select;
    };
    const tableBody = h('tbody', null, rows.length === 0 ? h('tr', null, h('td', { class: 'empty', colspan: 7 }, t('modpack.empty'))) : null, rows.map((row, i) => h('tr', null,
      h('td', null, String(i + 1)),
      h('td', null, cell(row, 'packageId', { 'aria-label': t('modpack.packageId'), required: true })),
      h('td', null, cell(row, 'version', { 'aria-label': t('modpack.version'), placeholder: t('public.anyVersion') })),
      h('td', null, cell(row, 'workshopId', { 'aria-label': t('modpack.workshopId'), inputmode: 'numeric' })),
      h('td', null, cell(row, 'url', { 'aria-label': t('public.link') })),
      h('td', null, kindSelect(row)),
      h('td', null, h('div', { class: 'row' },
        h('button', { type: 'button', 'aria-label': t('modpack.up'), 'data-move': 'up', onClick: () => move(i, -1) }, '↑'),
        h('button', { type: 'button', 'aria-label': t('modpack.down'), 'data-move': 'down', onClick: () => move(i, 1) }, '↓'),
        h('button', { type: 'button', class: 'danger', 'aria-label': t('modpack.remove'), onClick: () => { rows.splice(i, 1); draw(); } }, '×'))))));
    const add = h('button', { type: 'button' }, t('modpack.add'));
    add.addEventListener('click', () => { rows.push({ packageId: '', version: '', workshopId: '', url: '', kind: 'Required' }); draw(); });

    replace(body, 
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null, ['#', t('modpack.packageId'), t('modpack.version'), t('modpack.workshopId'), t('public.link'), t('public.kind'), ''].map(label => h('th', null, label)))),
        tableBody)),
      h('div', { class: 'actions' }, add, busyButton(t('common.save'), save, { class: 'primary' })));
  }

  function entries() {
    return rows.filter(r => r.packageId.trim()).map((r, i) => ({
      position: i,
      packageId: r.packageId.trim(),
      version: (r.version ?? '').trim(),
      workshopId: Number(String(r.workshopId ?? '').trim() || 0),
      url: (r.url ?? '').trim(),
      kind: r.kind,
    }));
  }

  function diff(next) {
    const key = e => e.packageId.toLowerCase();
    const before = new Map(original.map(e => [key(e), e]));
    const after = new Map(next.map(e => [key(e), e]));
    const added = next.filter(e => !before.has(key(e))).map(e => e.packageId);
    const removed = original.filter(e => !after.has(key(e))).map(e => e.packageId);
    const changed = next.filter(e => {
      const old = before.get(key(e));
      return old && (old.position !== e.position || (old.version ?? '') !== e.version || Number(old.workshopId) !== e.workshopId || (old.url ?? '') !== e.url || old.kind !== e.kind);
    }).map(e => e.packageId);
    return [
      added.length ? t('modpack.added', { list: added.join(', ') }) : null,
      removed.length ? t('modpack.removed', { list: removed.join(', ') }) : null,
      changed.length ? t('modpack.changed', { list: changed.join(', ') }) : null,
    ].filter(Boolean).join(' ') || t('modpack.noChanges');
  }

  async function save() {
    const next = entries();
    if (next.some(e => !Number.isSafeInteger(e.workshopId) || e.workshopId < 0)) {
      banner(t('modpack.badWorkshopId'));
      return;
    }
    if (!await confirmDialog({ title: t('modpack.saveTitle', { count: next.length }), text: diff(next), danger: false })) return;
    await api.put('modpack', { entries: next });
    banner(t('modpack.saved'), 'info');
    await load();
  }

  async function load() {
    try {
      const pack = await api.get('modpack');
      original = pack.entries.map(e => ({ ...e, workshopId: Number(e.workshopId) }));
      rows = pack.entries.map(e => ({ ...e, workshopId: e.workshopId ? String(e.workshopId) : '' }));
      snapshot = JSON.stringify(rows);
      draw();
    } catch (error) {
      showError(error);
    }
  }

  // Leaving the page with unsaved edits asks first; the hash router's own navigation is covered by the stop function.
  const beforeUnload = event => { if (dirty()) { event.preventDefault(); event.returnValue = ''; } };
  window.addEventListener('beforeunload', beforeUnload);
  load();
  return () => window.removeEventListener('beforeunload', beforeUnload);
}
