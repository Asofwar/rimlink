import { api } from '../api.js';
import { h, banner, showError, append } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';

/** The token form. The token goes to /api/panel/login once and is never stored; onSuccess opens the shell. */
export function render(root, onSuccess) {
  const input = h('input', { type: 'password', id: 'token', autocomplete: 'current-password', required: true });
  const submit = h('button', { type: 'submit', class: 'primary' }, t('login.submit'));
  const form = h('form', { class: 'login card' },
    h('h1', null, t('login.title')),
    h('label', { class: 'field', for: 'token' }, h('span', null, t('login.token')), input),
    h('p', { class: 'muted' }, t('login.hint')),
    submit);

  form.addEventListener('submit', async event => {
    event.preventDefault();
    submit.disabled = true;
    try {
      await api.panel.login(input.value);
      input.value = '';
      onSuccess();
    } catch (error) {
      if (error.status === 401) banner(t('login.wrong'));
      else if (error.status === 429) banner(t('login.lockedOut', { minutes: Math.max(1, Math.ceil((error.retryAfter ?? 600) / 60)) }));
      else showError(error);
    } finally {
      submit.disabled = false;
    }
  });

  append(root, h('div', { class: 'login-page' }, form));
  input.focus();
}
