import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { jsonResponse, meBody, mockApi, renderApp, resetSession, tokens } from '../test-utils';
import type { Role } from '../auth/AuthContext';
import { AppLayout } from './AppLayout';

afterEach(resetSession);

function setup(roles: Role[], routes: Record<string, () => Response>) {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  const api = mockApi({
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(roles)),
    ...routes,
  });
  renderApp(
    <Routes>
      <Route element={<AppLayout />}>
        <Route index element={<p>Home</p>} />
      </Route>
    </Routes>,
  );
  return api;
}

describe('AppLayout unread badge', () => {
  it('shows the unread count next to Outbox', async () => {
    setup(['LoanOfficer'], { 'GET /api/outbox/unread-count': () => jsonResponse(200, { count: 3 }) });

    const link = await screen.findByRole('link', { name: /Outbox/ });
    await screen.findByText('3');
    expect(link).toHaveTextContent('3 unread');
  });

  it('hides the badge at zero', async () => {
    setup(['LoanOfficer'], { 'GET /api/outbox/unread-count': () => jsonResponse(200, { count: 0 }) });

    const link = await screen.findByRole('link', { name: 'Outbox' });
    expect(link).not.toHaveTextContent('unread');
    expect(link.textContent).toBe('Outbox');
  });

  it('does not ask for the count when the user cannot see the outbox', async () => {
    const { calls } = setup(['Auditor'], {});

    await screen.findByRole('link', { name: 'Audit log' });
    expect(calls.some((c) => c.key.includes('unread-count'))).toBe(false);
  });
});
