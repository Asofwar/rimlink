import { api, query, ApiError } from '../api.js';
import { h, append, replace, section, field, table, busyButton, showError, confirmDialog } from '../../shared/dom.js';
import { input } from '../ui.js';
import { t } from '../../shared/i18n.js';

const MAX_INPUT = 1024 * 1024;
export async function render(root) {
  let current = null, guild = null, frozen = null, originalInput = null, disposed = false, generation = 0;
  const diagnostics = h('div', { 'aria-live': 'polite' }), rows = h('div'), result = h('pre', { class: 'diff', 'aria-live': 'polite' });
  const owner = input({ type: 'number', min: 1 }), operation = input({ maxlength: 256 });
  const reason = input({ maxlength: 1024, required: true });
  const guildReport = h('pre', { class: 'diff', 'aria-live': 'polite' });
  const campaign = h('select');
  const campaignField = field(t('outposts.guildCampaign'), campaign);
  const battle = h('select');
  const battleField = field(t('outposts.guildStartedBattle'), battle);
  const tile = input({ type: 'number', min: 0, max: 2147483647, step: 1, required: true });
  const resource = h('select', null, h('option', { value: '1' }, t('outposts.guildSteel')),
    h('option', { value: '2' }, t('outposts.guildWood')));
  const placementFields = h('div', { class: 'row' }, field(t('outposts.guildTile'), tile), field(t('outposts.guildResource'), resource));
  const inspectGuild = busyButton(t('outposts.refresh'), async () => {
    clearGuild();
    const report = await api.get('guild-capture?limit=100');
    if (disposed || !permission()) return;
    guild = report;
    replace(guildReport, JSON.stringify(report, null, 2));
    const campaigns = (report.campaigns ?? []).filter(row => ['Scheduled', 'PreparingBattle'].includes(row.phase));
    replace(campaign, h('option', { value: '' }, t('outposts.guildChooseCampaign')),
      ...campaigns.map(row => h('option', { value: row.id }, row.id + ' · ' + row.scheduledAtUtc)));
    const battles = (report.battles ?? []).filter(row => ['StartCommitted', 'AwaitingResult'].includes(row.phase));
    replace(battle, h('option', { value: '' }, t('outposts.guildChooseBattle')),
      ...battles.map(row => h('option', { value: row.id }, row.id + ' · ' + row.executorId)));
  });
  inspectGuild.disabled = true;
  const manifest = input({ maxlength: 36 }), digest = input({ maxlength: 64 });
  const draftFile = h('input', { type: 'file', accept: '.json,application/json' });
  const sidecarFile = h('input', { type: 'file', accept: '.bin,application/octet-stream' });
  const manifestFile = h('input', { type: 'file', accept: '.pb,.bin,application/octet-stream' });
  const action = h('select', null, [['initialize', 'outposts.action.initialize'], ['activate-domain-format', 'outposts.action.activate-domain-format'],
    ['activate-guild-domain-format', 'outposts.action.activate-guild-domain-format'],
    ['confirm-manifest', 'outposts.action.confirm-manifest'], ['resolve', 'outposts.action.resolve'],
    ['cancel-guild-campaign', 'outposts.action.cancel-guild-campaign'],
    ['abort-guild-battle', 'outposts.action.abort-guild-battle'],
    ['freeze-guild-capture', 'outposts.action.freeze-guild-capture'],
    ['unfreeze-guild-capture', 'outposts.action.unfreeze-guild-capture'],
    ['place-guild-outpost', 'outposts.action.place-guild-outpost']].map(([value, label]) => h('option', { value }, t(label))));
  const previewText = h('textarea', { readonly: true, rows: 12, class: 'outpost-command', 'aria-label': t('outposts.command') });
  const confirm = h('button', { type: 'button', class: 'primary', disabled: true }, t('outposts.confirm'));
  const save = h('button', { type: 'button', disabled: true }, t('outposts.saveCommand'));
  const preview = busyButton(t('outposts.preview'), prepare);
  const next = h('button', { type: 'button', disabled: true }, t('outposts.next'));
  next.addEventListener('click', async () => { next.disabled = true; await load(current?.nextCursor); next.disabled = current?.hasMore !== true; });
  const summary = h('div');

  function invalidate() {
    generation++; frozen = null; confirm.disabled = true; save.disabled = true; previewText.value = '';
  }
  function clearGuild() {
    guild = null; replace(guildReport); replace(campaign, h('option', { value: '' }, t('outposts.guildChooseCampaign')));
    replace(battle, h('option', { value: '' }, t('outposts.guildChooseBattle')));
    if (frozen?.request || action.value === 'cancel-guild-campaign') invalidate();
  }
  function updateFields() {
    campaignField.hidden = action.value !== 'cancel-guild-campaign';
    battleField.hidden = action.value !== 'abort-guild-battle';
    placementFields.hidden = action.value !== 'place-guild-outpost';
  }
  for (const control of [owner, operation, reason, manifest, digest, action, draftFile, sidecarFile, campaign, battle, tile, resource]) control.addEventListener('input', invalidate);
  action.addEventListener('input', updateFields);
  clearGuild(); updateFields();
  owner.addEventListener('input', () => { current = null; }); operation.addEventListener('input', () => { current = null; });
  append(root, h('h1', null, t('nav.outposts')), h('p', null, t('outposts.warning')),
    section(t('outposts.readiness'), diagnostics,
      h('div', { class: 'row' }, field(t('outposts.owner'), owner), field(t('outposts.operation'), operation), busyButton(t('outposts.refresh'), () => load())), rows, next),
    section(t('outposts.guildCapture'), inspectGuild, guildReport),
    section(t('outposts.manifest'), h('div', { class: 'row' }, field(t('outposts.manifestId'), manifest), field(t('outposts.digest'), digest),
      busyButton(t('outposts.inspectManifest'), async () => {
        const response = await api.get('outposts/manifests/' + encodeURIComponent(manifest.value));
        if (disposed) return;
        digest.value = response.digest; invalidate(); replace(summary, h('pre', { class: 'diff' }, JSON.stringify(response, null, 2)));
      })), summary, field(t('outposts.exportFile'), manifestFile), busyButton(t('outposts.upload'), upload)),
    section(t('outposts.control'), h('p', { class: 'muted' }, t('outposts.controlHelp')),
      h('div', { class: 'row' }, field(t('outposts.action'), action), field(t('outposts.reason'), reason),
        campaignField, battleField), placementFields,
      field(t('outposts.resolveFile'), draftFile), field(t('outposts.originalInput'), sidecarFile),
      h('div', { class: 'actions' }, preview), field(t('outposts.command'), previewText),
      h('div', { class: 'actions' }, save, confirm), result));

  function permission() { return current?.canAdmin === true; }
  function yes(value) { return t(value === true ? 'outposts.yes' : value === false ? 'outposts.no' : 'outposts.unknown'); }
  async function load(cursor) {
    try {
      const value = await api.get('outposts' + query({ playerId: owner.value, operationId: operation.value, limit: 50, cursor }));
      if (disposed) return;
      if (current?.observationDigest !== value.observationDigest) invalidate();
      current = value; const r = value.readiness;
      replace(diagnostics,
        h('p', { class: 'badge warn' }, t(r?.mode === 2 ? 'outposts.isolated' : r?.mode === 1 ? 'outposts.product' : 'outposts.unknown')),
        table([{ key: 'name', label: t('outposts.check') }, { key: 'value', label: t('outposts.value') }], [
          { name: t('outposts.configured'), value: yes(r?.configuredEnabled) },
          { name: t('outposts.qualification'), value: t(r?.buildQualification?.state === 1 ? 'outposts.qualified' : r?.buildQualification?.state === 0 ? 'outposts.pending' : 'outposts.unknown') },
          { name: t('outposts.advertised'), value: yes(r?.advertiseCapability) },
          { name: t('outposts.recovery'), value: yes(r?.canResolveCustody) },
          { name: t('outposts.history'), value: yes(r?.historyProtectionRequired) },
          { name: t('outposts.startup'), value: JSON.stringify(value.startup) },
          { name: t('outposts.head'), value: value.observation?.committed?.headId ?? t('outposts.unknown') }]),
        h('h3', null, t('outposts.integrations')),
        table([{ key: 'id', label: t('outposts.check') }, { key: 'revision', label: t('outposts.revision') },
          { label: t('outposts.state'), render: row => t(['outposts.unknown', 'outposts.implemented', 'outposts.unavailable'][row.state] ?? 'outposts.unknown') }], r?.integrationStates ?? []),
        h('h3', null, t('outposts.blockers')), h('pre', { class: 'diff' }, (value.blockers ?? [t('outposts.unknown')]).join('\n')),
        h('h3', null, t('outposts.preflights')), h('p', null, t('outposts.preflightHelp')),
        table([{ key: 'action', label: t('outposts.action') }, { label: t('outposts.state'), render: row => yes(row.allowed) },
          { label: t('outposts.blockers'), render: row => (row.reasons ?? []).join(', ') }], value.preflights ?? []));
      replace(rows, table([{ key: 'kind', label: t('outposts.kind') }, { key: 'id', label: t('outposts.id') },
        { key: 'playerId', label: t('outposts.owner') }, { key: 'state', label: t('outposts.state') },
        { label: t('outposts.evidence'), render: row => h('details', null, h('summary', null, row.evidenceState), h('pre', { class: 'diff' }, JSON.stringify(row, null, 2))) }], value.rows ?? []));
      next.disabled = !value.hasMore;
      preview.disabled = !permission();
      inspectGuild.disabled = !permission();
    } catch (error) {
      if (!disposed) { invalidate(); current = null; preview.disabled = true; inspectGuild.disabled = true; clearGuild(); replace(diagnostics, h('p', { role: 'alert' }, t('outposts.unreachable'))); showError(error); }
    }
  }
  async function fileBytes(control, limit) {
    const file = control.files?.[0];
    if (!file || file.size < 1 || file.size > limit) throw new ApiError(400, t('outposts.fileBound', { limit }));
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (bytes.length > limit) throw new ApiError(400, t('outposts.fileBound', { limit })); return bytes;
  }
  function base64(bytes) {
    let text = ''; for (let i = 0; i < bytes.length; i += 8192) text += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(text);
  }
  async function prepare() {
    if (!permission() || !reason.reportValidity()) return;
    invalidate(); const selectedGeneration = generation; originalInput = null;
    if (action.value === 'place-guild-outpost') {
      if (!tile.reportValidity()) return;
      if (!reason.value.trim() || reason.value.length > 500) throw new ApiError(400, t('outposts.guildPlacementReason'));
      const value = await api.post('guild-capture/place/preview', { tile: tile.valueAsNumber, resource: Number(resource.value), reason: reason.value });
      if (disposed || generation !== selectedGeneration) return;
      frozen = { ...value, guildPlacement: true };
      previewText.value = JSON.stringify(value, null, 2); save.disabled = false; replace(result); return;
    }
    if (action.value === 'cancel-guild-campaign') {
      if (!guild?.scope || !guild.headId || !campaign.value || !reason.value.trim() || reason.value.length > 500)
        throw new ApiError(400, t('outposts.guildCancelInputs'));
      const request = { scope: guild.scope, expectedHeadId: guild.headId, campaignId: campaign.value,
        requestId: crypto.randomUUID(), reason: reason.value };
      frozen = { request }; previewText.value = JSON.stringify(request, null, 2); save.disabled = false;
      replace(result); return;
    }
    if (action.value === 'abort-guild-battle') {
      if (!guild?.scope || !guild.headId || !battle.value || !reason.value.trim() || reason.value.length > 500)
        throw new ApiError(400, t('outposts.guildAbortInputs'));
      const request = { scope: guild.scope, expectedHeadId: guild.headId, battleId: battle.value,
        requestId: crypto.randomUUID(), reason: reason.value };
      frozen = { request, guildAbort: true }; previewText.value = JSON.stringify(request, null, 2); save.disabled = false;
      replace(result); return;
    }
    if (action.value === 'freeze-guild-capture' || action.value === 'unfreeze-guild-capture') {
      const desired = action.value === 'freeze-guild-capture';
      if (!guild?.scope || !guild.headId || guild.frozen === desired || !reason.value.trim() || reason.value.length > 500)
        throw new ApiError(400, t('outposts.guildFreezeInputs'));
      const request = { scope: guild.scope, expectedHeadId: guild.headId, requestId: crypto.randomUUID(),
        frozen: desired, reason: reason.value };
      frozen = { request, guildFreeze: true }; previewText.value = JSON.stringify(request, null, 2); save.disabled = false;
      replace(result); return;
    }
    const request = { action: action.value, requestId: crypto.randomUUID(), reason: reason.value };
    if (action.value === 'confirm-manifest') { request.manifestId = manifest.value; request.manifestDigest = digest.value; }
    if (action.value === 'resolve') {
      const selected = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(await fileBytes(draftFile, 96 * 1024)));
      if (selected.resolution && !selected.recovery) request.resolution = selected.resolution;
      else if (selected.recovery && !selected.resolution) request.recovery = selected.recovery;
      else throw new ApiError(400, t('outposts.resolveChoice'));
      if (sidecarFile.files?.length) { originalInput = base64(await fileBytes(sidecarFile, MAX_INPUT)); request.originalInput = originalInput; }
    }
    const value = await api.post('outposts/preview', request);
    if (disposed || generation !== selectedGeneration) return;
    replace(result, (value.blockers ?? []).join('\n'));
    if (!value.command || value.blockers?.length) return;
    frozen = value; previewText.value = JSON.stringify({ intentDigest: value.intentDigest, intent: value.intent }, null, 2); save.disabled = false;
  }
  function retainedBody(selected) {
    return selected.request ?? { command: selected.command, intentDigest: selected.intentDigest, confirmed: true,
      ...(originalInput ? { originalInput } : {}) };
  }
  save.addEventListener('click', () => {
    if (!frozen) return;
    const body = retainedBody(frozen);
    const url = URL.createObjectURL(new Blob([JSON.stringify(body, null, 2)], { type: 'application/json' }));
    const file = frozen.guildFreeze ? 'guild-capture-freeze.json' : frozen.guildAbort ? 'guild-battle-abort.json' : frozen.request ? 'guild-campaign-cancel.json'
      : frozen.guildPlacement ? 'guild-placement.json' : 'outpost-command.json';
    const link = h('a', { download: file });
    link.href = url; link.click(); URL.revokeObjectURL(url); confirm.disabled = false;
  });
  confirm.addEventListener('click', async () => {
    if (!frozen || confirm.disabled || !permission()) return;
    const selected = frozen; confirm.disabled = true;
    const text = selected.guildFreeze ? t(selected.request.frozen ? 'outposts.guildFreezeConfirm' : 'outposts.guildUnfreezeConfirm')
      : selected.guildAbort ? t('outposts.guildAbortConfirm', { id: selected.request.battleId })
      : selected.request ? t('outposts.guildCancelConfirm', { id: selected.request.campaignId })
      : selected.guildPlacement ? t('outposts.guildPlaceConfirm', { tile: selected.command.tile,
        resource: t(selected.command.resource === 1 ? 'outposts.guildSteel' : 'outposts.guildWood') })
      : t('outposts.confirmHelp', { digest: selected.intentDigest });
    if (!await confirmDialog({ title: t('outposts.confirm'), text, confirmLabel: t('outposts.confirm') })) { confirm.disabled = false; return; }
    if (frozen !== selected) return;
    const route = { initialize: 'initialize', 'activate-domain-format': 'activate-domain-format',
      'activate-guild-domain-format': 'activate-domain-format', 'confirm-manifest': 'manifests/confirm', resolve: 'resolve' }[action.value];
    try {
      const path = selected.guildFreeze ? 'guild-capture/freeze' : selected.guildAbort ? 'guild-capture/abort' : selected.request ? 'guild-capture/cancel'
        : selected.guildPlacement ? 'guild-capture/place' : 'outposts/' + route;
      const response = await api.post(path, retainedBody(selected));
      if (disposed) return; if (selected.request || selected.guildPlacement) clearGuild();
      invalidate(); replace(result, JSON.stringify(response, null, 2)); await load();
    } catch (error) { if (!disposed) { replace(result, t('outposts.uncertain')); showError(error); } }
  });
  async function upload() {
    if (!permission()) return; invalidate();
    const bytes = await fileBytes(manifestFile, 24 * 1024 * 1024), uploadId = crypto.randomUUID();
    const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)); let response;
    for (let offset = 0; offset < bytes.length; offset += 256 * 1024) {
      const chunk = bytes.subarray(offset, offset + 256 * 1024);
      response = await api.post('outposts/manifests/chunks', { uploadId, sha256: base64(hash), totalBytes: bytes.length, offset, data: base64(chunk) });
      if (disposed) return;
      if (response.receivedBytes !== offset + chunk.length || Boolean(response.complete) !== (offset + chunk.length === bytes.length)) throw new ApiError(400, t('outposts.uploadMismatch'));
    }
    const expected = Array.from(hash, b => b.toString(16).padStart(2, '0')).join('');
    if (response.digest !== expected) throw new ApiError(400, t('outposts.uploadMismatch'));
    manifest.value = response.manifestId; digest.value = response.digest; action.value = 'confirm-manifest'; updateFields();
    replace(result, t('outposts.staged')); await load();
  }
  await load(); return () => { disposed = true; invalidate(); originalInput = null; };
}
