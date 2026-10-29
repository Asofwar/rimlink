import { lang, t } from './i18n.js';

export function fmtDate(iso) {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString(lang());
}

export function fmtBytes(bytes) {
  if (bytes === null || bytes === undefined) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

export function fmtDuration(seconds) {
  if (seconds === null || seconds === undefined) return '—';
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return t('format.daysHours', { d: days, h: hours });
  if (hours > 0) return t('format.hoursMinutes', { h: hours, m: minutes });
  return t('format.minutes', { m: minutes });
}

export function fmtSilver(amount) {
  return amount === null || amount === undefined ? '—' : Number(amount).toLocaleString(lang());
}
