import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { RequestDetailPage } from './RequestDetailPage';
import type { CollectionStatus, InviteItem, Request, RequestItemStatus } from './requests';

afterEach(resetSession);

function item(status: RequestItemStatus): InviteItem {
  return {
    id: 'it1',
    name: 'Bank statement',
    kind: 'FormField',
    instructions: '',
    allowMultiple: false,
    fieldType: 'Text',
    isRequired: true,
    sortOrder: 0,
    status,
    rejectionReason: null,
    fieldValue: 'x',
    partyNote: null,
    reviewedAt: null,
    files: [],
  };
}

function request(status: CollectionStatus, itemStatus: RequestItemStatus): Request {
  return {
    id: 'l1',
    reference: 'LN-1',
    name: 'Acme',
    amount: 10,
    requestType: 'SBA',
    status,
    createdAt: '2027-01-05T10:00:00Z',
    completedAt: status === 'Completed' ? '2027-01-09T10:00:00Z' : null,
    invites: [
      {
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
        status,
        hasPassword: false,
        items: [item(itemStatus)],
      },
    ],
  };
}

function setup(roles: Role[], data: Request, extra: Record<string, () => Response> = {}) {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  const mock = mockApi({
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(roles)),
    'GET /api/requests/l1': () => jsonResponse(200, data),
    'GET /api/contacts?page=1&pageSize=100': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 100 }),
    'GET /api/templates?page=1&pageSize=100': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 100 }),
    'GET /api/requests?page=1&pageSize=25': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 25 }),
    ...extra,
  });
  renderApp(
    <Routes>
      <Route path="/requests/:id" element={<RequestDetailPage />} />
    </Routes>,
    '/requests/l1',
  );
  return mock;
}

describe('request completion', () => {
  it('shows the Ready to complete badge and enables Complete request', async () => {
    const { calls } = setup(['Requester'], request('ReadyToComplete', 'Approved'), {
      'POST /api/requests/l1/complete': () => jsonResponse(200, {}),
    });
    const user = userEvent.setup();

    const button = await screen.findByRole('button', { name: 'Complete request' });
    expect(screen.getAllByText('Ready to complete').length).toBeGreaterThan(0);
    expect(button).toBeEnabled();

    await user.click(button);
    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/requests/l1/complete')).toBe(true));
  });

  it('disables Complete request until everything is approved', async () => {
    setup(['Requester'], request('Submitted', 'Submitted'));

    expect(await screen.findByRole('button', { name: 'Complete request' })).toBeDisabled();
  });

  it('shows a refused completion to the user', async () => {
    setup(['Requester'], request('ReadyToComplete', 'Approved'), {
      'POST /api/requests/l1/complete': () =>
        jsonResponse(409, { title: 'Conflict', detail: 'Not everything is approved.' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Complete request' }));

    expect(await screen.findByText('Not everything is approved.')).toBeInTheDocument();
  });

  it('locks a Completed request and explains why', async () => {
    setup(['Requester'], request('Completed', 'Approved'));

    expect(await screen.findByText(/completed and locked/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reject Bank statement' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Complete request' })).not.toBeInTheDocument();
  });

  it('disables approve on a Completed request', async () => {
    setup(['Requester'], request('Completed', 'Submitted'));

    expect(await screen.findByRole('button', { name: 'Approve Bank statement' })).toBeDisabled();
  });

  it('asks for a reason before reopening', async () => {
    const { calls } = setup(['Requester'], request('Completed', 'Approved'), {
      'POST /api/requests/l1/reopen': () => jsonResponse(200, {}),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reopen request' }));
    const send = screen.getByRole('button', { name: 'Reopen' });
    expect(send).toBeDisabled();

    await user.type(screen.getByLabelText(/Reason for reopening/), ' Missing page ');
    await user.click(send);

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/requests/l1/reopen')).toBe(true));
    const call = calls.find((c) => c.key === 'POST /api/requests/l1/reopen');
    expect(JSON.parse(String(call?.init?.body))).toEqual({ reason: 'Missing page' });
  });

  it('shows a refused reopen to the user', async () => {
    setup(['Requester'], request('Completed', 'Approved'), {
      'POST /api/requests/l1/reopen': () => jsonResponse(409, { title: 'Conflict', detail: 'Request is not completed.' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reopen request' }));
    await user.type(screen.getByLabelText(/Reason for reopening/), 'Because');
    await user.click(screen.getByRole('button', { name: 'Reopen' }));

    expect(await screen.findByText('Request is not completed.')).toBeInTheDocument();
  });

  it('hides the completion actions from read-only roles', async () => {
    setup(['Auditor'], request('ReadyToComplete', 'Approved'));

    await screen.findByText('Ann Lee');
    expect(screen.queryByRole('button', { name: 'Complete request' })).not.toBeInTheDocument();
  });
});
