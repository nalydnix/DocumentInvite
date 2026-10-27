import type { ReactNode } from 'react';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider, type Role } from './auth/AuthContext';
import { clearSession } from './lib/api';

export function jsonResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

export const tokens = { accessToken: 'access-1', refreshToken: 'refresh-1' };

export function meBody(roles: Role[], displayName = 'Test User') {
  return { id: 'u1', email: 'test@example.com', displayName, roles, accessExpiresAt: null };
}

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response>;

/** Replaces fetch with a handler keyed by "METHOD /path". Unknown calls fail the test. */
export function mockApi(routes: Record<string, Handler>) {
  const calls: { key: string; init?: RequestInit }[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const key = `${init?.method ?? 'GET'} ${url}`;
    calls.push({ key, init });
    const handler = routes[key];
    if (!handler) throw new Error(`Unexpected request: ${key}`);
    return handler(url, init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock };
}

export function resetSession() {
  clearSession();
  vi.unstubAllGlobals();
}

export function renderApp(ui: ReactNode, route = '/') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <AuthProvider>{ui}</AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
