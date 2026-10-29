import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { UsersPage } from './UsersPage';

afterEach(resetSession);

const ann = {
  id: 'u1',
  email: 'ann@example.com',
  displayName: 'Ann Admin',
  role: 'Admin',
  accessExpiresAt: null,
  isLockedOut: false,
};
const bob = { ...ann, id: 'u2', email: 'bob@example.com', displayName: 'Bob Requester', role: 'Requester' };

const page = (items: unknown[], total = items.length, pageNo = 1, pageSize = 25) => ({
  items,
  total,
  page: pageNo,
  pageSize,
});

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Admin'])),
  };
}

function renderRoutes(route: string) {
  return renderApp(
    <Routes>
      <Route path="/users" element={<UsersPage />} />
    </Routes>,
    route,
  );
}

describe('UsersPage pagination', () => {
  it('opens the page and size given in the URL', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/users?page=2&pageSize=50': () => jsonResponse(200, page([bob], 60, 2, 50)),
    });

    renderRoutes('/users?page=2&pageSize=50');

    expect(await screen.findByText('Bob Requester')).toBeInTheDocument();
    expect(screen.getByText(/Showing 51.60 of 60/)).toBeInTheDocument();
  });

  it('navigates to the next page', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, page([ann], 30, 1)),
      'GET /api/users?page=2&pageSize=25': () => jsonResponse(200, page([bob], 30, 2)),
    });
    renderRoutes('/users');

    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Bob Requester')).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'GET /api/users?page=2&pageSize=25')).toBe(true);
  });

  it('falls back to the last page when the page is past the end', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/users?page=9&pageSize=25': () => jsonResponse(200, page([], 30, 9)),
      'GET /api/users?page=2&pageSize=25': () => jsonResponse(200, page([bob], 30, 2)),
    });

    renderRoutes('/users?page=9');

    expect(await screen.findByText('Bob Requester')).toBeInTheDocument();
    expect(calls.some((c) => c.key.includes('page=2'))).toBe(true);
  });

  it('shows an empty state when there are no users', async () => {
    mockApi({ ...signedIn(), 'GET /api/users?page=1&pageSize=25': () => jsonResponse(200, page([])) });

    renderRoutes('/users');

    expect(await screen.findByText('No users yet.')).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Pagination' })).not.toBeInTheDocument();
  });

  it('shows an error when the list cannot be loaded', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/users?page=1&pageSize=25': () => jsonResponse(500, { title: 'Server error' }),
    });

    renderRoutes('/users');

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
