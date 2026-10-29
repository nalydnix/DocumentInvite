import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { RequestDetailPage } from './RequestDetailPage';
import { RequestFormPage } from './RequestFormPage';
import { RequestsPage } from './RequestsPage';
import type { Invite, Request, RequestSummary } from './requests';

afterEach(resetSession);

const summary: RequestSummary = {
  id: 'l1',
  reference: 'LN-1001',
  name: 'Acme acquisition',
  amount: 1250000,
  requestType: 'SBA 7(a)',
  status: 'InProgress',
  createdAt: '2027-01-05T10:00:00Z',
  completedAt: null,
  partyCount: 2,
  itemsTotal: 4,
  itemsApproved: 1,
  percentComplete: 25,
  dueDate: '2027-02-01',
  isOverdue: false,
};

const annInvite: Invite = {
  id: 'i1',
  requestId: 'l1',
  contactId: 'c1',
  partyName: 'Ann Lee',
  partyEmail: 'ann@example.com',
  partyType: 'Borrower',
  dueDate: '2027-02-01',
  reminderIntervalDays: 7,
  sentAt: '2027-01-05T10:00:00Z',
  tokenExpiresAt: '2027-03-03T00:00:00Z',
  revokedAt: null,
  status: 'Submitted',
  hasPassword: false,
  items: [
    {
      id: 'it1',
      name: 'Tax return',
      kind: 'FileUpload',
      instructions: '',
      allowMultiple: false,
      fieldType: null,
      isRequired: true,
      sortOrder: 0,
      status: 'Approved',
      rejectionReason: null,
      fieldValue: null,
      partyNote: null,
      reviewedAt: null,
      files: [],
    },
    {
      id: 'it2',
      name: 'Bank statement',
      kind: 'FileUpload',
      instructions: '',
      allowMultiple: true,
      fieldType: null,
      isRequired: true,
      sortOrder: 1,
      status: 'Submitted',
      rejectionReason: null,
      fieldValue: null,
      partyNote: null,
      reviewedAt: null,
      files: [
        {
          id: 'f1',
          fileName: 'LN-1001_Lee_Bank-statement_1.pdf',
          originalFileName: 'scan.pdf',
          contentType: 'application/pdf',
          sizeBytes: 2048,
          sequence: 1,
          uploadedAt: '2027-01-06T10:00:00Z',
        },
      ],
    },
  ],
};

const request: Request = { ...summary, invites: [annInvite] };

const page = (items: RequestSummary[], total = items.length) => ({ items, total, page: 1, pageSize: 25 });

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
      <Route path="/requests" element={<RequestsPage />} />
      <Route path="/requests/new" element={<RequestFormPage />} />
      <Route path="/requests/:id" element={<RequestDetailPage />} />
    </Routes>,
    route,
  );
}

describe('RequestsPage', () => {
  it('lists requests with status and a create link for writers', async () => {
    mockApi({
      ...signedIn(['Requester']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
    });

    renderRoutes('/requests');

    const list = await screen.findByRole('list', { name: 'Requests' });
    expect(within(list).getByRole('link', { name: /LN-1001/ })).toHaveAttribute('href', '/requests/l1');
    expect(within(list).getByText('In progress')).toBeInTheDocument();
    expect(within(list).getByText(/\$1,250,000\.00/)).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'New request' })).toBeInTheDocument();
  });

  it('filters by status', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
      'GET /api/requests?page=1&pageSize=25&status=Completed': () => jsonResponse(200, page([])),
    });
    renderRoutes('/requests');
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Status'), 'Completed');

    expect(await screen.findByText('No requests match your search.')).toBeInTheDocument();
    expect(calls.some((c) => c.key.endsWith('status=Completed'))).toBe(true);
  });

  it('pages through results using the shared pagination', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, { items: [summary], total: 26, page: 1, pageSize: 25 }),
      'GET /api/requests?page=2&pageSize=25': () =>
        jsonResponse(200, { items: [{ ...summary, id: 'l2', reference: 'LN-2002' }], total: 26, page: 2, pageSize: 25 }),
    });
    renderRoutes('/requests');
    const user = userEvent.setup();

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText(/LN-2002/)).toBeInTheDocument();
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument();
  });

  it('returns to page 1 when the status filter changes', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/requests?page=2&pageSize=25': () => jsonResponse(200, { items: [summary], total: 30, page: 2, pageSize: 25 }),
      'GET /api/requests?page=1&pageSize=25&status=Sent': () => jsonResponse(200, page([summary])),
    });
    renderRoutes('/requests?page=2');
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Status'), 'Sent');

    await waitFor(() => expect(calls.some((c) => c.key === 'GET /api/requests?page=1&pageSize=25&status=Sent')).toBe(true));
  });

  it('says so when there are no requests', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });

    renderRoutes('/requests');

    expect(await screen.findByText('No requests yet.')).toBeInTheDocument();
  });
});

describe('RequestFormPage', () => {
  it('creates a request and opens it', async () => {
    const { calls } = mockApi({
      ...signedIn(['Requester']),
      'POST /api/requests': () => jsonResponse(201, { ...request, invites: [] }),
      'GET /api/requests/l1': () => jsonResponse(200, { ...request, invites: [] }),
      'GET /api/contacts?page=1&pageSize=100': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 100 }),
      'GET /api/templates?page=1&pageSize=100': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 100 }),
    });
    renderRoutes('/requests/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Reference'), 'LN-1001');
    await user.type(screen.getByLabelText('Request name'), 'Acme acquisition');
    await user.type(screen.getByLabelText('Amount (USD)'), '1250000.5');
    await user.type(screen.getByLabelText('Request type'), 'SBA 7(a)');
    await user.click(screen.getByRole('button', { name: 'Create request' }));

    const post = calls.find((c) => c.key === 'POST /api/requests');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      reference: 'LN-1001',
      name: 'Acme acquisition',
      amount: 1250000.5,
      requestType: 'SBA 7(a)',
    });
    expect(await screen.findByRole('heading', { name: /LN-1001/ })).toBeInTheDocument();
  });

  it('shows the API errors, such as a duplicate reference', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'POST /api/requests': () => jsonResponse(409, { title: 'Conflict', detail: 'A request with this reference already exists.' }),
    });
    renderRoutes('/requests/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Reference'), 'LN-1001');
    await user.type(screen.getByLabelText('Request name'), 'Acme');
    await user.type(screen.getByLabelText('Amount (USD)'), '10');
    await user.type(screen.getByLabelText('Request type'), 'CRE');
    await user.click(screen.getByRole('button', { name: 'Create request' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('already exists');
  });

});

describe('RequestDetailPage', () => {
  const contacts = {
    items: [
      { id: 'c1', firstName: 'Ann', lastName: 'Lee', email: 'ann@example.com', phone: null, partyType: 'Borrower', isPseudonymised: false },
      { id: 'c2', firstName: 'Bob', lastName: 'Adams', email: 'bob@example.com', phone: null, partyType: 'Guarantor', isPseudonymised: false },
    ],
    total: 2,
    page: 1,
    pageSize: 100,
  };
  const templates = [{ id: 't1', name: 'SBA checklist', description: '', isSeeded: true, isArchived: false, itemCount: 2 }];
  const templateDetail = {
    id: 't1',
    name: 'SBA checklist',
    items: [
      { id: 'ti1', name: 'Tax return', kind: 'FileUpload', instructions: 'Two years', allowMultiple: false, fieldType: null, isRequired: true },
      { id: 'ti2', name: 'Years in business', kind: 'FormField', instructions: '', allowMultiple: false, fieldType: 'Number', isRequired: false },
    ],
  };

  function routes(roles: Role[], current: Request = request) {
    return {
      ...signedIn(roles),
      'GET /api/requests/l1': () => jsonResponse(200, current),
      'GET /api/contacts?page=1&pageSize=100': () => jsonResponse(200, contacts),
      'GET /api/templates?page=1&pageSize=100': () => jsonResponse(200, { items: templates, total: 1, page: 1, pageSize: 100 }),
      'GET /api/templates/t1': () => jsonResponse(200, templateDetail),
    };
  }

  it('shows the request, its status and each party with progress', async () => {
    mockApi(routes(['Requester']));

    renderRoutes('/requests/l1');

    expect(await screen.findByRole('heading', { name: /LN-1001/ })).toBeInTheDocument();
    const parties = screen.getByRole('list', { name: 'Parties' });
    expect(within(parties).getByText('Ann Lee')).toBeInTheDocument();
    expect(within(parties).getAllByText('Submitted').length).toBeGreaterThan(0);
    expect(within(parties).getByText(/1 of 2 items approved/)).toBeInTheDocument();
  });

  it('downloads the approved zip for a requester', async () => {
    const createObjectURL = vi.fn(() => 'blob:x');
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }));
    const { calls } = mockApi({
      ...routes(['Requester']),
      'GET /api/requests/l1/export': () => new Response('zip', { status: 200 }),
    });
    renderRoutes('/requests/l1');

    await userEvent.click(await screen.findByRole('button', { name: 'Download approved' }));

    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
    expect(calls.some((c) => c.key === 'GET /api/requests/l1/export')).toBe(true);
  });

  it('explains when there is nothing approved to export', async () => {
    mockApi({
      ...routes(['Admin']),
      'GET /api/requests/l1/export': () =>
        jsonResponse(409, { title: 'Conflict', detail: 'This request has no approved documents or answers to export yet.' }),
    });
    renderRoutes('/requests/l1');

    await userEvent.click(await screen.findByRole('button', { name: 'Download approved' }));

    expect(await screen.findByText(/no approved documents/)).toBeInTheDocument();
  });

  it('revokes a link', async () => {
    let revoked = false;
    const { calls } = mockApi({
      ...routes(['Requester']),
      'GET /api/requests/l1': () =>
        jsonResponse(200, { ...request, invites: [{ ...annInvite, revokedAt: revoked ? '2027-01-06T00:00:00Z' : null }] }),
      'POST /api/invites/i1/revoke': () => {
        revoked = true;
        return jsonResponse(200, { ...annInvite, revokedAt: '2027-01-06T00:00:00Z' });
      },
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Revoke link for Ann Lee' }));

    expect(await screen.findByText('Link revoked')).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'POST /api/invites/i1/revoke')).toBe(true);
    expect(screen.queryByRole('button', { name: 'Revoke link for Ann Lee' })).not.toBeInTheDocument();
  });

  it('reissues a link and says the old one stopped working', async () => {
    const { calls } = mockApi({
      ...routes(['Admin']),
      'POST /api/invites/i1/reissue': () => jsonResponse(200, annInvite),
    });
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reissue link for Ann Lee' }));

    expect(await screen.findByText(/old link no longer works/)).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'POST /api/invites/i1/reissue')).toBe(true);
  });

  it('sends invites with the template items the requester selected and edited', async () => {
    const { calls } = mockApi({
      ...routes(['Requester'], { ...request, invites: [] }),
      'POST /api/requests/l1/invites': () => jsonResponse(201, [annInvite]),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));
    expect(screen.getByRole('button', { name: 'Send invites' })).toBeDisabled();
    await user.selectOptions(screen.getByLabelText('Party 1'), 'c1');
    await user.selectOptions(screen.getByLabelText('Template'), 't1');
    const name = await screen.findByLabelText('Name of Tax return');
    await user.clear(name);
    await user.type(name, 'Business tax return');
    await user.click(screen.getByLabelText('Include Years in business'));
    await user.type(screen.getByLabelText('Due date'), '2027-02-01');
    await user.type(screen.getByLabelText('Reminder every (days)'), '3');
    await user.click(screen.getByRole('button', { name: 'Send invites' }));

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/requests/l1/invites')).toBe(true));
    const post = calls.find((c) => c.key === 'POST /api/requests/l1/invites');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      parties: [
        {
          contactId: 'c1',
          dueDate: '2027-02-01',
          reminderIntervalDays: 3,
          password: null,
          items: [
            {
              name: 'Business tax return',
              kind: 'FileUpload',
              instructions: 'Two years',
              allowMultiple: false,
              fieldType: null,
              isRequired: true,
            },
          ],
        },
      ],
    });
    expect(await screen.findByRole('status')).toHaveTextContent('Invites sent');
  });

  it('sends the optional portal password with the party', async () => {
    const { calls } = mockApi({
      ...routes(['Requester'], { ...request, invites: [] }),
      'POST /api/requests/l1/invites': () => jsonResponse(201, [annInvite]),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));
    await user.selectOptions(screen.getByLabelText('Party 1'), 'c1');
    await user.selectOptions(screen.getByLabelText('Template'), 't1');
    await screen.findByLabelText('Name of Tax return');
    await user.type(screen.getByLabelText('Due date'), '2027-02-01');
    await user.type(screen.getByLabelText(/Portal password/), 'correct-horse');
    await user.click(screen.getByRole('button', { name: 'Send invites' }));

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/requests/l1/invites')).toBe(true));
    const body = JSON.parse(calls.find((c) => c.key === 'POST /api/requests/l1/invites')!.init!.body as string);
    expect(body.parties[0].password).toBe('correct-horse');
  });

  it('does not offer a party that was already invited', async () => {
    mockApi(routes(['Requester']));
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));

    const select = screen.getByLabelText('Party 1');
    expect(within(select).queryByRole('option', { name: /Ann Lee/ })).not.toBeInTheDocument();
    expect(within(select).getByRole('option', { name: /Bob Adams/ })).toBeInTheDocument();
  });

  it('shows the API error when sending fails', async () => {
    mockApi({
      ...routes(['Requester'], { ...request, invites: [] }),
      'POST /api/requests/l1/invites': () =>
        jsonResponse(400, { title: 'Validation', errors: { 'parties[0].dueDate': ['The due date cannot be in the past.'] } }),
    });
    renderRoutes('/requests/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));
    await user.selectOptions(screen.getByLabelText('Party 1'), 'c2');
    await user.selectOptions(screen.getByLabelText('Template'), 't1');
    await screen.findByLabelText('Name of Tax return');
    await user.type(screen.getByLabelText('Due date'), '2020-01-01');
    await user.click(screen.getByRole('button', { name: 'Send invites' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cannot be in the past');
  });

  it('says so when the request does not exist', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/requests/l9': () => jsonResponse(404, { title: 'Not Found' }),
    });

    renderRoutes('/requests/l9');

    expect(await screen.findByText('This request does not exist.')).toBeInTheDocument();
  });
});

describe('RequestsPage dashboard', () => {
  it('shows progress, due date and an overdue flag on each request', async () => {
    mockApi({
      ...signedIn(['Requester']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([{ ...summary, isOverdue: true }])),
    });

    renderRoutes('/requests');

    const list = await screen.findByRole('list', { name: 'Requests' });
    expect(within(list).getByRole('progressbar', { name: 'Progress for LN-1001' })).toHaveAttribute('aria-valuenow', '25');
    expect(within(list).getByText(/25% complete \(1 of 4 items approved\)/)).toBeInTheDocument();
    expect(within(list).getByText(/due Feb 1, 2027/)).toBeInTheDocument();
    expect(within(list).getByText('Overdue')).toBeInTheDocument();
  });

  it('does not flag a request that is not overdue', async () => {
    mockApi({
      ...signedIn(['Requester']),
      'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
    });

    renderRoutes('/requests');

    await screen.findByRole('list', { name: 'Requests' });
    expect(screen.queryByText('Overdue')).not.toBeInTheDocument();
  });
});
