import { api } from '../api.js';
import { h, showError, append } from '../../shared/dom.js';
import { t } from '../../shared/i18n.js';

/** The token form. The token goes to /api/panel/login once and is never stored; onSuccess opens the shell. */
export function render(root, onSuccess) {
  const input = h('input', { type: 'password', id: 'token', autocomplete: 'off', required: true, 'aria-describedby': 'token-error' });
  const error = h('p', { class: 'field-error', id: 'token-error', role: 'alert' });
  const submit = h('button', { type: 'submit', class: 'primary' }, t('login.submit'));
  const form = h('form', { class: 'login card' },
    h('h1', null, t('login.title')),
    h('label', { class: 'field', for: 'token' }, h('span', null, t('login.token')), input),
    error,
    h('p', { class: 'muted' }, t('login.hint')),
    submit);

  const fail = message => {
    error.textContent = message;
    input.setAttribute('aria-invalid', 'true');
    input.focus();
  };

  form.addEventListener('submit', async event => {
    event.preventDefault();
    error.textContent = '';
    input.removeAttribute('aria-invalid');
    submit.disabled = true;
    try {
      await api.panel.login(input.value);
      input.value = '';
      onSuccess();
    } catch (failure) {
      if (failure.status === 401) fail(t('login.wrong'));
      else if (failure.status === 429) fail(t('login.lockedOut', { minutes: Math.max(1, Math.ceil((failure.retryAfter ?? 600) / 60)) }));
      else showError(failure);
    } finally {
      submit.disabled = false;
    }
  });

  append(root, h('main', { class: 'login-page' }, form));
  input.focus();
}
