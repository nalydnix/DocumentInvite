import { ApiError, apiFetch, hasStoredSession, signIn } from './api';
import { jsonResponse, mockApi, resetSession, tokens } from '../test-utils';

afterEach(resetSession);

describe('api client', () => {
  it('keeps the refresh token in sessionStorage and never in localStorage', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(200, tokens) });

    await signIn('a@example.com', 'Correct-Horse-12');

    expect(sessionStorage.getItem('di.refreshToken')).toBe('refresh-1');
    expect(localStorage.length).toBe(0);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('access-1');
  });

  it('sends the access token as a bearer header', async () => {
    const { calls } = mockApi({
      'POST /api/auth/login': () => jsonResponse(200, tokens),
      'GET /api/me': () => jsonResponse(200, { id: 'u1' }),
    });

    await signIn('a@example.com', 'Correct-Horse-12');
    await apiFetch('/api/me');

    expect((calls[1].init?.headers as Record<string, string>).Authorization).toBe('Bearer access-1');
  });

  it('refreshes the tokens once on a 401 and retries the request', async () => {
    let meCalls = 0;
    const { calls } = mockApi({
      'POST /api/auth/login': () => jsonResponse(200, tokens),
      'GET /api/me': () => (++meCalls === 1 ? jsonResponse(401) : jsonResponse(200, { id: 'u1' })),
      'POST /api/auth/refresh': () => jsonResponse(200, { accessToken: 'access-2', refreshToken: 'refresh-2' }),
    });

    await signIn('a@example.com', 'Correct-Horse-12');
    const me = await apiFetch<{ id: string }>('/api/me');

    expect(me.id).toBe('u1');
    expect(sessionStorage.getItem('di.refreshToken')).toBe('refresh-2');
    expect((calls.at(-1)?.init?.headers as Record<string, string>).Authorization).toBe('Bearer access-2');
  });

  it('ends the session when the refresh is refused', async () => {
    mockApi({
      'POST /api/auth/login': () => jsonResponse(200, tokens),
      'GET /api/me': () => jsonResponse(401),
      'POST /api/auth/refresh': () => jsonResponse(401),
    });
    await signIn('a@example.com', 'Correct-Horse-12');

    await expect(apiFetch('/api/me')).rejects.toBeInstanceOf(ApiError);

    expect(hasStoredSession()).toBe(false);
  });

  it('explains a locked account', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(401, { title: 'Unauthorized', detail: 'LockedOut' }) });

    await expect(signIn('a@example.com', 'x')).rejects.toThrow(/temporarily locked/);
  });

  it('does not reveal whether the e-mail or the password was wrong', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(401, { title: 'Unauthorized', detail: 'Failed' }) });

    await expect(signIn('a@example.com', 'x')).rejects.toThrow('Invalid email or password.');
  });
});
