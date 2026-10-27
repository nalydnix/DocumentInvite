import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, renderApp, resetSession, tokens, meBody } from '../test-utils';
import { UsersPage } from './UsersPage';

afterEach(resetSession);

const auditor = {
  id: 'a1',
  email: 'aud@example.com',
  displayName: 'Ann Auditor',
  roles: ['Auditor'],
  accessExpiresAt: '2030-01-15T12:00:00.000Z',
  isLockedOut: false,
};
const viewer = { ...auditor, id: 'v1', email: 'view@example.com', displayName: 'Vic Viewer', roles: ['Viewer'], accessExpiresAt: null };

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Admin'])),
  };
}

describe('UsersPage', () => {
  it('lists users and shows the expiry editor for auditors only', async () => {
    mockApi({ ...signedIn(), 'GET /api/users': () => jsonResponse(200, [auditor, viewer]) });

    renderApp(<UsersPage />);

    const list = await screen.findByRole('list', { name: 'Users' });
    expect(within(list).getByText('Ann Auditor')).toBeInTheDocument();
    expect(within(list).getByLabelText('Access expiry for Ann Auditor')).toBeInTheDocument();
    expect(within(list).queryByLabelText('Access expiry for Vic Viewer')).not.toBeInTheDocument();
  });

  it('creates a user with the chosen roles', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users': () => jsonResponse(200, []),
      'POST /api/users': () => jsonResponse(201, viewer),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Display name'), 'New Person');
    await user.type(screen.getByLabelText(/Initial password/), 'Another-Strong-Pw-1');
    await user.click(screen.getByLabelText('LoanOfficer'));
    await user.click(screen.getByRole('button', { name: 'Create user' }));

    const post = calls.find((c) => c.key === 'POST /api/users');
    expect(JSON.parse(post!.init!.body as string)).toMatchObject({
      email: 'new@example.com',
      displayName: 'New Person',
      roles: ['LoanOfficer'],
      accessExpiresAt: null,
    });
  });

  it('shows the API validation errors when creation fails', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/users': () => jsonResponse(200, []),
      'POST /api/users': () =>
        jsonResponse(400, { title: 'Validation', errors: { password: ['Passwords must be at least 12 characters.'] } }),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Display name'), 'New Person');
    await user.type(screen.getByLabelText(/Initial password/), 'short');
    await user.click(screen.getByRole('button', { name: 'Create user' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('at least 12 characters');
  });

  it('saves changed roles for a user', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users': () => jsonResponse(200, [viewer]),
      'PUT /api/users/v1/roles': () => jsonResponse(200, { ...viewer, roles: ['Viewer', 'LoanOfficer'] }),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    const row = (await screen.findByText('Vic Viewer')).closest('li')!;
    await user.click(within(row).getByLabelText('LoanOfficer'));
    await user.click(within(row).getByRole('button', { name: 'Save roles for Vic Viewer' }));

    const put = calls.find((c) => c.key === 'PUT /api/users/v1/roles');
    expect(JSON.parse(put!.init!.body as string)).toEqual({ roles: ['Viewer', 'LoanOfficer'] });
  });

  it('clears an auditor expiry date', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users': () => jsonResponse(200, [auditor]),
      'PUT /api/users/a1/access-expiry': () => jsonResponse(200, { ...auditor, accessExpiresAt: null }),
    });
    renderApp(<UsersPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Clear expiry for Ann Auditor' }));

    const put = calls.find((c) => c.key === 'PUT /api/users/a1/access-expiry');
    expect(JSON.parse(put!.init!.body as string)).toEqual({ accessExpiresAt: null });
  });
});
