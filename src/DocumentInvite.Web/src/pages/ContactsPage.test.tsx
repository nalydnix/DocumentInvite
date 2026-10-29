import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { ContactFormPage } from './ContactFormPage';
import { ContactsPage } from './ContactsPage';
import type { Contact } from './contacts';

afterEach(resetSession);

const ann: Contact = {
  id: 'c1',
  firstName: 'Ann',
  lastName: 'Lee',
  email: 'ann@example.com',
  phone: '+1 555 010 2030',
  partyType: 'CoBorrower',
  isPseudonymised: false,
};
const bob: Contact = { ...ann, id: 'c2', firstName: 'Bob', lastName: 'Adams', email: 'bob@example.com', phone: null, partyType: 'Broker' };

const page = (items: Contact[], total = items.length, pageNo = 1) => ({ items, total, page: pageNo, pageSize: 25 });

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
      <Route path="/contacts/new" element={<ContactFormPage />} />
      <Route path="/contacts/:id" element={<ContactFormPage />} />
    </Routes>,
    route,
  );
}

describe('ContactsPage', () => {
  it('lists contacts with their party type and a create link for writers', async () => {
    mockApi({
      ...signedIn(['Requester']),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([ann, bob])),
    });

    renderRoutes('/contacts');

    const list = await screen.findByRole('list', { name: 'Contacts' });
    expect(within(list).getByText('Ann Lee')).toBeInTheDocument();
    expect(within(list).getByText('Co-borrower')).toBeInTheDocument();
    expect(within(list).getByText('Broker')).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'New contact' })).toBeInTheDocument();
    expect(within(list).getByRole('link', { name: 'Edit Ann Lee' })).toHaveAttribute('href', '/contacts/c1');
  });

  it('searches by the typed term', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([ann, bob])),
      'GET /api/contacts?page=1&pageSize=25&search=bob': () => jsonResponse(200, page([bob])),
    });
    renderRoutes('/contacts');

    await userEvent.type(await screen.findByLabelText('Search contacts'), 'bob');

    await waitFor(() => expect(screen.queryByText('Ann Lee')).not.toBeInTheDocument());
    expect(screen.getByText('Bob Adams')).toBeInTheDocument();
    expect(calls.some((c) => c.key.endsWith('search=bob'))).toBe(true);
  });

  it('shows an empty state and pages through results', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([ann], 26)),
      'GET /api/contacts?page=2&pageSize=25': () => jsonResponse(200, page([bob], 26, 2)),
    });
    renderRoutes('/contacts');
    const user = userEvent.setup();

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Bob Adams')).toBeInTheDocument();
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('says so when there are no contacts', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });

    renderRoutes('/contacts');

    expect(await screen.findByText('No contacts yet.')).toBeInTheDocument();
  });
});

describe('ContactFormPage', () => {
  it('creates a contact and returns to the list', async () => {
    const { calls } = mockApi({
      ...signedIn(['Requester']),
      'POST /api/contacts': () => jsonResponse(201, ann),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([ann])),
    });
    renderRoutes('/contacts/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('First name'), 'Ann');
    await user.type(screen.getByLabelText('Last name'), 'Lee');
    await user.type(screen.getByLabelText('Email'), 'ann@example.com');
    await user.selectOptions(screen.getByLabelText('Party type'), 'Guarantor');
    await user.click(screen.getByRole('button', { name: 'Create contact' }));

    const post = calls.find((c) => c.key === 'POST /api/contacts');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      firstName: 'Ann',
      lastName: 'Lee',
      email: 'ann@example.com',
      phone: null,
      partyType: 'Guarantor',
    });
    expect(await screen.findByRole('list', { name: 'Contacts' })).toBeInTheDocument();
  });

  it('shows the API validation errors', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'POST /api/contacts': () =>
        jsonResponse(400, { title: 'Validation', errors: { phone: ["'Phone' must be a valid phone number."] } }),
    });
    renderRoutes('/contacts/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('First name'), 'Ann');
    await user.type(screen.getByLabelText('Last name'), 'Lee');
    await user.type(screen.getByLabelText('Email'), 'ann@example.com');
    await user.type(screen.getByLabelText('Phone (optional)'), 'abc');
    await user.click(screen.getByRole('button', { name: 'Create contact' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('valid phone number');
  });

  it('loads a contact for editing and saves the change', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts/c1': () => jsonResponse(200, ann),
      'PUT /api/contacts/c1': () => jsonResponse(200, { ...ann, firstName: 'Anna' }),
      'GET /api/contacts?page=1&pageSize=25': () => jsonResponse(200, page([ann])),
    });
    renderRoutes('/contacts/c1');
    const user = userEvent.setup();

    const first = await screen.findByLabelText('First name');
    await waitFor(() => expect(first).toHaveValue('Ann'));
    expect(screen.getByLabelText('Party type')).toHaveValue('CoBorrower');
    await user.clear(first);
    await user.type(first, 'Anna');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const put = calls.find((c) => c.key === 'PUT /api/contacts/c1');
    expect(JSON.parse(put!.init!.body as string)).toMatchObject({ firstName: 'Anna', phone: '+1 555 010 2030' });
  });

  it('keeps a pseudonymised contact read-only', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/contacts/c1': () => jsonResponse(200, { ...ann, isPseudonymised: true }),
    });

    renderRoutes('/contacts/c1');

    expect(await screen.findByLabelText('First name')).toBeDisabled();
    expect(screen.getByText(/pseudonymised/)).toBeInTheDocument();
  });
});
