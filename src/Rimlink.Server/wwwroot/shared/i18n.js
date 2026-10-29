// English and Russian strings from /i18n/<lang>.json. The choice is remembered per browser when storage is available.
const STORAGE_KEY = 'rimlink.lang';
let current = 'en';
let strings = {};

function preferred() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved === 'en' || saved === 'ru') return saved;
  } catch {
    // Storage blocked (private window): fall back to the browser language.
  }
  return (navigator.language || '').toLowerCase().startsWith('ru') ? 'ru' : 'en';
}

export async function loadI18n(code) {
  current = code ?? preferred();
  const response = await fetch(`/i18n/${current}.json`, { credentials: 'same-origin' });
  strings = await response.json();
  document.documentElement.lang = current;
}

export async function setLang(code) {
  try {
    localStorage.setItem(STORAGE_KEY, code);
  } catch {
    // Not remembered; still switch for this page.
  }
  await loadI18n(code);
}

export function lang() {
  return current;
}

/** The string for key with {name} placeholders filled; a missing key shows the key itself. */
export function t(key, params) {
  let text = strings[key] ?? key;
  if (params) for (const [name, value] of Object.entries(params)) text = text.replaceAll(`{${name}}`, String(value));
  return text;
}
