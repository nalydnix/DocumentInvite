import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, renderApp, resetSession, tokens, meBody } from '../test-utils';
import { UsersPage } from './UsersPage';

afterEach(resetSession);

const auditor = {
  id: 'a1',
  email: 'aud@example.com',
  displayName: 'Ann Auditor',
  role: 'Auditor',
  accessExpiresAt: '2030-01-15T12:00:00.000Z',
  isLockedOut: false,
};
const requester = { ...auditor, id: 'v1', email: 'view@example.com', displayName: 'Vic Requester', role: 'Requester', accessExpiresAt: null };

const paged = (items: unknown[], total = items.length, page = 1, pageSize = 25) => ({ items, total, page, pageSize });

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Admin'])),
  };
}

describe('UsersPage', () => {
  it('lists users and shows the expiry editor for auditors only', async () => {
    mockApi({ ...signedIn(), 'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([auditor, requester])) });

    renderApp(<UsersPage />);

    const list = await screen.findByRole('list', { name: 'Users' });
    expect(within(list).getByText('Ann Auditor')).toBeInTheDocument();
    expect(within(list).getByLabelText('Access expiry for Ann Auditor')).toBeInTheDocument();
    expect(within(list).queryByLabelText('Access expiry for Vic Requester')).not.toBeInTheDocument();
  });

  it('creates a user with the chosen role', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([])),
      'POST /api/users': () => jsonResponse(201, requester),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Display name'), 'New Person');
    await user.type(screen.getByLabelText(/Initial password/), 'Another-Strong-Pw-1');
    await user.selectOptions(screen.getByLabelText('Role'), 'Requester');
    await user.click(screen.getByRole('button', { name: 'Create user' }));

    const post = calls.find((c) => c.key === 'POST /api/users');
    expect(JSON.parse(post!.init!.body as string)).toMatchObject({
      email: 'new@example.com',
      displayName: 'New Person',
      role: 'Requester',
      accessExpiresAt: null,
    });
  });

  it('shows the API validation errors when creation fails', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([])),
      'POST /api/users': () =>
        jsonResponse(400, { title: 'Validation', errors: { password: ['Passwords must be at least 12 characters.'] } }),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Display name'), 'New Person');
    await user.type(screen.getByLabelText(/Initial password/), 'short');
    await user.selectOptions(screen.getByLabelText('Role'), 'Requester');
    await user.click(screen.getByRole('button', { name: 'Create user' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('at least 12 characters');
  });

  it('saves a changed role for a user', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([requester])),
      'PUT /api/users/v1/role': () => jsonResponse(200, { ...requester, role: 'Admin' }),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    const row = (await screen.findByText('Vic Requester')).closest('li')!;
    await user.selectOptions(within(row).getByLabelText('Role for Vic Requester'), 'Admin');
    await user.click(within(row).getByRole('button', { name: 'Save role for Vic Requester' }));

    const put = calls.find((c) => c.key === 'PUT /api/users/v1/role');
    expect(JSON.parse(put!.init!.body as string)).toEqual({ role: 'Admin' });
  });

  it('offers a single-choice role and requires one before creating', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([requester])),
    });
    renderApp(<UsersPage />);
    const user = userEvent.setup();

    const select = await screen.findByLabelText('Role');
    expect(select.tagName).toBe('SELECT');
    expect(select).toBeRequired();
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    await user.type(screen.getByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Display name'), 'New Person');
    await user.type(screen.getByLabelText(/Initial password/), 'Another-Strong-Pw-1');
    await user.click(screen.getByRole('button', { name: 'Create user' }));
    expect(calls.find((c) => c.key === 'POST /api/users')).toBeUndefined();

    const row = screen.getByText('Vic Requester').closest('li')!;
    expect(within(row).getByLabelText('Role for Vic Requester')).toHaveValue('Requester');
    expect(within(row).getByRole('button', { name: 'Save role for Vic Requester' })).toBeDisabled();
  });

  it('clears an auditor expiry date', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, paged([auditor])),
      'PUT /api/users/a1/access-expiry': () => jsonResponse(200, { ...auditor, accessExpiresAt: null }),
    });
    renderApp(<UsersPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Clear expiry for Ann Auditor' }));

    const put = calls.find((c) => c.key === 'PUT /api/users/a1/access-expiry');
    expect(JSON.parse(put!.init!.body as string)).toEqual({ accessExpiresAt: null });
  });
});
