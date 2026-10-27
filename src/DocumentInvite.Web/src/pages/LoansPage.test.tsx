import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { LoanDetailPage } from './LoanDetailPage';
import { LoanFormPage } from './LoanFormPage';
import { LoansPage } from './LoansPage';
import type { Invite, Loan, LoanSummary } from './loans';

afterEach(resetSession);

const summary: LoanSummary = {
  id: 'l1',
  reference: 'LN-1001',
  name: 'Acme acquisition',
  amount: 1250000,
  loanType: 'SBA 7(a)',
  status: 'InProgress',
  createdAt: '2027-01-05T10:00:00Z',
  completedAt: null,
  partyCount: 2,
};

const annInvite: Invite = {
  id: 'i1',
  loanId: 'l1',
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
    },
  ],
};

const loan: Loan = { ...summary, invites: [annInvite] };

const page = (items: LoanSummary[], total = items.length) => ({ items, total, page: 1, pageSize: 25 });

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
      <Route path="/loans" element={<LoansPage />} />
      <Route path="/loans/new" element={<LoanFormPage />} />
      <Route path="/loans/:id" element={<LoanDetailPage />} />
    </Routes>,
    route,
  );
}

describe('LoansPage', () => {
  it('lists loans with status and a create link for writers', async () => {
    mockApi({
      ...signedIn(['LoanOfficer']),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
    });

    renderRoutes('/loans');

    const list = await screen.findByRole('list', { name: 'Loans' });
    expect(within(list).getByRole('link', { name: /LN-1001/ })).toHaveAttribute('href', '/loans/l1');
    expect(within(list).getByText('In progress')).toBeInTheDocument();
    expect(within(list).getByText(/\$1,250,000\.00/)).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'New loan' })).toBeInTheDocument();
  });

  it('hides the create link from a viewer', async () => {
    mockApi({
      ...signedIn(['Viewer']),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
    });

    renderRoutes('/loans');

    await screen.findByRole('list', { name: 'Loans' });
    expect(screen.queryByRole('link', { name: 'New loan' })).not.toBeInTheDocument();
  });

  it('filters by status', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([summary])),
      'GET /api/loans?page=1&pageSize=25&status=Completed': () => jsonResponse(200, page([])),
    });
    renderRoutes('/loans');
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Status'), 'Completed');

    expect(await screen.findByText('No loans match your search.')).toBeInTheDocument();
    expect(calls.some((c) => c.key.endsWith('status=Completed'))).toBe(true);
  });

  it('pages through results using the shared pagination', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, { items: [summary], total: 26, page: 1, pageSize: 25 }),
      'GET /api/loans?page=2&pageSize=25': () =>
        jsonResponse(200, { items: [{ ...summary, id: 'l2', reference: 'LN-2002' }], total: 26, page: 2, pageSize: 25 }),
    });
    renderRoutes('/loans');
    const user = userEvent.setup();

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText(/LN-2002/)).toBeInTheDocument();
    expect(screen.getByText('Page 2 of 2')).toBeInTheDocument();
  });

  it('returns to page 1 when the status filter changes', async () => {
    const { calls } = mockApi({
      ...signedIn(['Admin']),
      'GET /api/loans?page=2&pageSize=25': () => jsonResponse(200, { items: [summary], total: 30, page: 2, pageSize: 25 }),
      'GET /api/loans?page=1&pageSize=25&status=Sent': () => jsonResponse(200, page([summary])),
    });
    renderRoutes('/loans?page=2');
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Status'), 'Sent');

    await waitFor(() => expect(calls.some((c) => c.key === 'GET /api/loans?page=1&pageSize=25&status=Sent')).toBe(true));
  });

  it('says so when there are no loans', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });

    renderRoutes('/loans');

    expect(await screen.findByText('No loans yet.')).toBeInTheDocument();
  });
});

describe('LoanFormPage', () => {
  it('creates a loan and opens it', async () => {
    const { calls } = mockApi({
      ...signedIn(['LoanOfficer']),
      'POST /api/loans': () => jsonResponse(201, { ...loan, invites: [] }),
      'GET /api/loans/l1': () => jsonResponse(200, { ...loan, invites: [] }),
      'GET /api/contacts?page=1&pageSize=100': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 100 }),
      'GET /api/templates': () => jsonResponse(200, []),
    });
    renderRoutes('/loans/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Reference'), 'LN-1001');
    await user.type(screen.getByLabelText('Loan name'), 'Acme acquisition');
    await user.type(screen.getByLabelText('Amount (USD)'), '1250000.5');
    await user.type(screen.getByLabelText('Loan type'), 'SBA 7(a)');
    await user.click(screen.getByRole('button', { name: 'Create loan' }));

    const post = calls.find((c) => c.key === 'POST /api/loans');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      reference: 'LN-1001',
      name: 'Acme acquisition',
      amount: 1250000.5,
      loanType: 'SBA 7(a)',
    });
    expect(await screen.findByRole('heading', { name: /LN-1001/ })).toBeInTheDocument();
  });

  it('shows the API errors, such as a duplicate reference', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'POST /api/loans': () => jsonResponse(409, { title: 'Conflict', detail: 'A loan with this reference already exists.' }),
    });
    renderRoutes('/loans/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Reference'), 'LN-1001');
    await user.type(screen.getByLabelText('Loan name'), 'Acme');
    await user.type(screen.getByLabelText('Amount (USD)'), '10');
    await user.type(screen.getByLabelText('Loan type'), 'CRE');
    await user.click(screen.getByRole('button', { name: 'Create loan' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('already exists');
  });

  it('does not let a viewer create loans', async () => {
    mockApi(signedIn(['Viewer']));

    renderRoutes('/loans/new');

    expect(await screen.findByText('Your role cannot create loans.')).toBeInTheDocument();
  });
});

describe('LoanDetailPage', () => {
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

  function routes(roles: Role[], current: Loan = loan) {
    return {
      ...signedIn(roles),
      'GET /api/loans/l1': () => jsonResponse(200, current),
      'GET /api/contacts?page=1&pageSize=100': () => jsonResponse(200, contacts),
      'GET /api/templates': () => jsonResponse(200, templates),
      'GET /api/templates/t1': () => jsonResponse(200, templateDetail),
    };
  }

  it('shows the loan, its status and each party with progress', async () => {
    mockApi(routes(['LoanOfficer']));

    renderRoutes('/loans/l1');

    expect(await screen.findByRole('heading', { name: /LN-1001/ })).toBeInTheDocument();
    const parties = screen.getByRole('list', { name: 'Parties' });
    expect(within(parties).getByText('Ann Lee')).toBeInTheDocument();
    expect(within(parties).getByText('Submitted')).toBeInTheDocument();
    expect(within(parties).getByText(/1 of 2 items approved/)).toBeInTheDocument();
  });

  it('shows a viewer the parties without any actions', async () => {
    mockApi(routes(['Viewer']));

    renderRoutes('/loans/l1');

    await screen.findByText('Ann Lee');
    expect(screen.queryByRole('button', { name: /Revoke link/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add party' })).not.toBeInTheDocument();
  });

  it('revokes a link', async () => {
    let revoked = false;
    const { calls } = mockApi({
      ...routes(['LoanOfficer']),
      'GET /api/loans/l1': () =>
        jsonResponse(200, { ...loan, invites: [{ ...annInvite, revokedAt: revoked ? '2027-01-06T00:00:00Z' : null }] }),
      'POST /api/invites/i1/revoke': () => {
        revoked = true;
        return jsonResponse(200, { ...annInvite, revokedAt: '2027-01-06T00:00:00Z' });
      },
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderRoutes('/loans/l1');
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
    renderRoutes('/loans/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reissue link for Ann Lee' }));

    expect(await screen.findByText(/old link no longer works/)).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'POST /api/invites/i1/reissue')).toBe(true);
  });

  it('sends invites with the template items the officer selected and edited', async () => {
    const { calls } = mockApi({
      ...routes(['LoanOfficer'], { ...loan, invites: [] }),
      'POST /api/loans/l1/invites': () => jsonResponse(201, [annInvite]),
      'GET /api/loans?page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderRoutes('/loans/l1');
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

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/loans/l1/invites')).toBe(true));
    const post = calls.find((c) => c.key === 'POST /api/loans/l1/invites');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      parties: [
        {
          contactId: 'c1',
          dueDate: '2027-02-01',
          reminderIntervalDays: 3,
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

  it('does not offer a party that was already invited', async () => {
    mockApi(routes(['LoanOfficer']));
    renderRoutes('/loans/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));

    const select = screen.getByLabelText('Party 1');
    expect(within(select).queryByRole('option', { name: /Ann Lee/ })).not.toBeInTheDocument();
    expect(within(select).getByRole('option', { name: /Bob Adams/ })).toBeInTheDocument();
  });

  it('shows the API error when sending fails', async () => {
    mockApi({
      ...routes(['LoanOfficer'], { ...loan, invites: [] }),
      'POST /api/loans/l1/invites': () =>
        jsonResponse(400, { title: 'Validation', errors: { 'parties[0].dueDate': ['The due date cannot be in the past.'] } }),
    });
    renderRoutes('/loans/l1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Add party' }));
    await user.selectOptions(screen.getByLabelText('Party 1'), 'c2');
    await user.selectOptions(screen.getByLabelText('Template'), 't1');
    await screen.findByLabelText('Name of Tax return');
    await user.type(screen.getByLabelText('Due date'), '2020-01-01');
    await user.click(screen.getByRole('button', { name: 'Send invites' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('cannot be in the past');
  });

  it('says so when the loan does not exist', async () => {
    mockApi({
      ...signedIn(['Admin']),
      'GET /api/loans/l9': () => jsonResponse(404, { title: 'Not Found' }),
    });

    renderRoutes('/loans/l9');

    expect(await screen.findByText('This loan does not exist.')).toBeInTheDocument();
  });
});
