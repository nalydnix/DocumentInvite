// Base URL of the API. Empty means same-origin (the dev proxy handles /api).
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL ?? '';

// The access token lives in memory only. The refresh token lives in sessionStorage, so it
// survives a reload of the tab but not closing it. Neither token is ever logged.
const REFRESH_TOKEN_KEY = 'di.refreshToken';

let accessToken: string | null = null;
let refreshInFlight: Promise<boolean> | null = null;
const sessionEndedListeners = new Set<() => void>();

function readRefreshToken(): string | null {
  try {
    return sessionStorage.getItem(REFRESH_TOKEN_KEY);
  } catch {
    return null;
  }
}

function writeRefreshToken(token: string | null): void {
  try {
    if (token) sessionStorage.setItem(REFRESH_TOKEN_KEY, token);
    else sessionStorage.removeItem(REFRESH_TOKEN_KEY);
  } catch {
    // Storage can be unavailable (private windows); the session then lasts until reload.
  }
}

export function hasStoredSession(): boolean {
  return readRefreshToken() !== null;
}

/** Forgets both tokens. Does not notify listeners. */
export function clearSession(): void {
  accessToken = null;
  writeRefreshToken(null);
}

/** Registers a callback for when the session ends because tokens were rejected. */
export function onSessionEnded(listener: () => void): () => void {
  sessionEndedListeners.add(listener);
  return () => sessionEndedListeners.delete(listener);
}

export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, title: string, detail?: string, fieldErrors: Record<string, string[]> = {}) {
    super(detail ?? title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.fieldErrors = fieldErrors;
  }

  /** All messages, field errors first, for showing in a form. */
  get messages(): string[] {
    const fromFields = Object.values(this.fieldErrors).flat();
    return fromFields.length > 0 ? fromFields : [this.detail ?? this.title];
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as {
      title?: string;
      detail?: string;
      errors?: Record<string, string[]>;
    };
    return new ApiError(
      response.status,
      problem.title ?? response.statusText,
      problem.detail,
      problem.errors ?? {},
    );
  } catch {
    return new ApiError(response.status, response.statusText || 'Request failed');
  }
}

interface TokenResponse {
  accessToken: string;
  refreshToken: string;
}

function storeTokens(tokens: TokenResponse): void {
  accessToken = tokens.accessToken;
  writeRefreshToken(tokens.refreshToken);
}

function post(path: string, body: unknown): Promise<Response> {
  return fetch(apiBaseUrl + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Signs in with e-mail and password. Throws an ApiError when the sign-in is refused. */
export async function signIn(email: string, password: string): Promise<void> {
  const response = await post('/api/auth/login', { email, password });
  if (!response.ok) {
    const error = await toApiError(response);
    // Identity answers 401 with "LockedOut", "NotAllowed" or "Failed" as the detail.
    const reason =
      error.detail === 'LockedOut'
        ? 'This account is temporarily locked after too many failed attempts. Try again in 15 minutes.'
        : error.detail === 'NotAllowed'
          ? 'Access for this account is not allowed or has expired.'
          : 'Invalid email or password.';
    throw new ApiError(response.status, 'Sign-in failed', reason);
  }
  storeTokens((await response.json()) as TokenResponse);
}

/** Exchanges the stored refresh token for new tokens. Calls made at the same time share one request. */
export function refreshSession(): Promise<boolean> {
  refreshInFlight ??= (async () => {
    const refreshToken = readRefreshToken();
    if (!refreshToken) return false;
    try {
      const response = await post('/api/auth/refresh', { refreshToken });
      if (!response.ok) {
        clearSession();
        return false;
      }
      storeTokens((await response.json()) as TokenResponse);
      return true;
    } catch {
      return false;
    }
  })().finally(() => {
    refreshInFlight = null;
  });
  return refreshInFlight;
}

function send(path: string, method: string, body: unknown): Promise<Response> {
  const headers: Record<string, string> = {};
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  return fetch(apiBaseUrl + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/**
 * Calls the API as the signed-in user. A 401 triggers one token refresh and one retry; if that
 * fails the session ends and listeners (the auth provider) are told.
 */
export async function apiFetch<T>(path: string, options: { method?: string; body?: unknown } = {}): Promise<T> {
  const method = options.method ?? 'GET';
  const hadSession = accessToken !== null || hasStoredSession();

  let response = await send(path, method, options.body);
  if (response.status === 401 && hadSession && (await refreshSession())) {
    response = await send(path, method, options.body);
  }

  if (response.status === 401 && hadSession) {
    clearSession();
    sessionEndedListeners.forEach((listener) => listener());
  }

  if (!response.ok) throw await toApiError(response);
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}
