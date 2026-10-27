import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { ContactsPage } from './ContactsPage';
import type { Contact } from './contacts';

afterEach(resetSession);

const ann: Contact = {
  id: 'c1',
  firstName: 'Ann',
  lastName: 'Lee',
  email: 'ann@example.com',
  phone: null,
  partyType: 'Borrower',
  isPseudonymised: false,
};
const bob: Contact = { ...ann, id: 'c2', firstName: 'Bob', lastName: 'Adams', email: 'bob@example.com' };

const page = (items: Contact[], total = items.length, pageNo = 1, pageSize = 25) => ({
  items,
  total,
  page: pageNo,
  pageSize,
});

function signedIn(roles: Role[]) {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(roles)),
  };
}

function renderRoutes(route: string) {
  return renderApp(
    <Routes>
      <Route path="/contacts" element={<ContactsPage />} />
    </Routes>,
    route,
  );
}

describe('ContactsPage pagination in the URL', () => {
  it('opens the page and size given in the URL', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=2&pageSize=50': () => jsonResponse(200, page([bob], 60, 2, 50)),
    });

    renderRoutes('/contacts?page=2&pageSize=50');

    expect(await screen.findByText('Bob Adams')).toBeInTheDocument();
    expect(screen.getByText(/Showing 51.60 of 60/)).toBeInTheDocument();
    expect(screen.getByLabelText('Rows per page')).toHaveValue('50');
  });

  it('falls back to the last page when the page is past the end', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=9&pageSize=25': () => jsonResponse(200, page([], 30, 9)),
      'GET /api/contacts?page=2&pageSize=25': () => jsonResponse(200, page([bob], 30, 2)),
    });

    renderRoutes('/contacts?page=9');

    expect(await screen.findByText('Bob Adams')).toBeInTheDocument();
    expect(calls.some((c) => c.key.includes('page=2'))).toBe(true);
  });

  it('starts a new search on page 1', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=2&pageSize=25': () => jsonResponse(200, page([ann], 30, 2)),
      'GET /api/contacts?page=1&pageSize=25&search=bob': () => jsonResponse(200, page([bob])),
    });
    renderRoutes('/contacts?page=2');

    await userEvent.type(await screen.findByLabelText('Search contacts'), 'bob');

    expect(await screen.findByText('Bob Adams')).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'GET /api/contacts?page=1&pageSize=25&search=bob')).toBe(true);
  });
});
