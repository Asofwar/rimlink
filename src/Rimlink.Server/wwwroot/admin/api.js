// The panel's only way to the server: /api/admin/* with the session cookie, the CSRF header and JSON.
export class ApiError extends Error {
  constructor(status, detail, retryAfter = null) {
    super(detail || `HTTP ${status}`);
    this.status = status;
    this.detail = detail;
    this.retryAfter = retryAfter;
  }
}

let unauthorizedRaised = false;

/** Allows the next 401 to raise rimlink:unauthorized again (after a successful login). */
export function resetUnauthorized() {
  unauthorizedRaised = false;
}

async function call(method, url, body) {
  const init = { method, credentials: 'same-origin', headers: { 'X-Rimlink-Panel': '1', Accept: 'application/json' } };
  if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  let response;
  try {
    response = await fetch(url, init);
  } catch {
    throw new ApiError(0, null);
  }
  if (response.status === 401 && url !== '/api/panel/login') {
    // Once per burst: a dozen pollers failing together must show the login screen once.
    if (!unauthorizedRaised) {
      unauthorizedRaised = true;
      window.dispatchEvent(new Event('rimlink:unauthorized'));
    }
    throw new ApiError(401, null);
  }
  if (!response.ok) {
    let detail = null;
    try {
      const problem = await response.json();
      detail = problem.detail || problem.title || null;
    } catch {
      // No JSON body.
    }
    throw new ApiError(response.status, detail, Number(response.headers.get('Retry-After')) || null);
  }
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

const base = '/api/admin/';

export const api = {
  get: path => call('GET', base + path),
  post: (path, body) => call('POST', base + path, body ?? {}),
  put: (path, body) => call('PUT', base + path, body),
  del: path => call('DELETE', base + path),
  panel: {
    login: token => call('POST', '/api/panel/login', { token }),
    logout: () => call('POST', '/api/panel/logout', {}),
    session: () => call('GET', '/api/panel/session'),
  },
};

/** Builds a query string from the defined values only. */
export function query(params) {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  const text = search.toString();
  return text ? `?${text}` : '';
}
