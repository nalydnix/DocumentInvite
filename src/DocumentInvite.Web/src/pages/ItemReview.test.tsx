import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import type { Role } from '../auth/AuthContext';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { RequestDetailPage } from './RequestDetailPage';
import type { Invite, InviteItem, Request } from './requests';

afterEach(resetSession);

const pdfItem: InviteItem = {
  id: 'it1',
  name: 'Bank statement',
  kind: 'FileUpload',
  instructions: '',
  allowMultiple: false,
  fieldType: null,
  isRequired: true,
  sortOrder: 0,
  status: 'Submitted',
  rejectionReason: null,
  fieldValue: null,
  partyNote: null,
  reviewedAt: null,
  files: [
    {
      id: 'f1',
      fileName: 'LN-1_Lee_Bank-statement_1.pdf',
      originalFileName: 'scan.pdf',
      contentType: 'application/pdf',
      sizeBytes: 2048,
      sequence: 1,
      uploadedAt: '2027-01-06T10:00:00Z',
    },
  ],
};

const imageItem: InviteItem = {
  ...pdfItem,
  id: 'it2',
  name: 'Photo ID',
  files: [{ ...pdfItem.files[0], id: 'f2', fileName: 'id.pdf', contentType: 'image/png' }],
};

const fieldItem: InviteItem = {
  ...pdfItem,
  id: 'it3',
  name: 'Years in business',
  kind: 'FormField',
  fieldType: 'Number',
  fieldValue: '7',
  files: [],
};

function requestWith(items: InviteItem[]): Request {
  const invite: Invite = {
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
    items,
  };
  return {
    id: 'l1',
    reference: 'LN-1',
    name: 'Acme',
    amount: 10,
    requestType: 'SBA',
    status: 'Submitted',
    createdAt: '2027-01-05T10:00:00Z',
    completedAt: null,
    invites: [invite],
  };
}

function setup(roles: Role[], request: Request, extra: Record<string, () => Response> = {}) {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  const mock = mockApi({
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(roles)),
    'GET /api/requests/l1': () => jsonResponse(200, request),
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

describe('item review', () => {
  it('shows each item with its status, answer and files', async () => {
    setup(['Requester'], requestWith([pdfItem, fieldItem]));

    const items = await screen.findByRole('list', { name: 'Items for Ann Lee' });

    expect(within(items).getByText('Bank statement')).toBeInTheDocument();
    expect(within(items).getByText('LN-1_Lee_Bank-statement_1.pdf', { exact: false })).toBeInTheDocument();
    expect(within(items).getByText('7')).toBeInTheDocument();
  });

  it('approves an item in one click', async () => {
    const { calls } = setup(['Requester'], requestWith([pdfItem]), {
      'POST /api/items/it1/approve': () => jsonResponse(200, { itemId: 'it1', itemStatus: 'Approved' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Approve Bank statement' }));

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/items/it1/approve')).toBe(true));
  });

  it('requires a reason before sending a rejection', async () => {
    const { calls } = setup(['Requester'], requestWith([pdfItem]), {
      'POST /api/items/it1/reject': () => jsonResponse(200, { itemId: 'it1', itemStatus: 'Rejected' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reject Bank statement' }));
    const send = screen.getByRole('button', { name: 'Send rejection' });
    expect(send).toBeDisabled();

    await user.type(screen.getByLabelText(/Reason for rejecting/), '  Page missing ');
    await user.click(send);

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/items/it1/reject')).toBe(true));
    const post = calls.find((c) => c.key === 'POST /api/items/it1/reject');
    expect(JSON.parse(post!.init!.body as string)).toEqual({ reason: 'Page missing' });
  });

  it('shows the API error when a rejection fails', async () => {
    setup(['Requester'], requestWith([pdfItem]), {
      'POST /api/items/it1/reject': () =>
        jsonResponse(502, { title: 'Bad Gateway', detail: 'The party could not be emailed, so the item was not rejected.' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reject Bank statement' }));
    await user.type(screen.getByLabelText(/Reason for rejecting/), 'Blurry');
    await user.click(screen.getByRole('button', { name: 'Send rejection' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be emailed');
  });

  it('previews a PDF inline and an image as an image', async () => {
    const { calls } = setup(['Requester'], requestWith([pdfItem, imageItem]), {
      'GET /api/files/f1/preview-url': () =>
        jsonResponse(200, { url: 'https://blob/f1?sp=r', expiresAt: '2027-01-01T00:05:00Z', fileName: 'x', contentType: 'application/pdf' }),
      'GET /api/files/f2/preview-url': () =>
        jsonResponse(200, { url: 'https://blob/f2?sp=r', expiresAt: '2027-01-01T00:05:00Z', fileName: 'x', contentType: 'image/png' }),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Preview LN-1_Lee_Bank-statement_1.pdf for Bank statement' }));
    const frame = await screen.findByTitle('Preview of LN-1_Lee_Bank-statement_1.pdf');
    expect(frame).toHaveAttribute('src', 'https://blob/f1?sp=r');

    await user.click(screen.getByRole('button', { name: 'Preview id.pdf for Photo ID' }));
    expect(await screen.findByAltText('Preview of id.pdf')).toHaveAttribute('src', 'https://blob/f2?sp=r');
    expect(calls.filter((c) => c.key.includes('preview-url'))).toHaveLength(2);
  });

  it('shows the rejection reason on a rejected item and offers no approve', async () => {
    setup(['Admin'], requestWith([{ ...pdfItem, status: 'Rejected', rejectionReason: 'Blurry' }]));

    expect(await screen.findByText('Rejected: Blurry')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Approve/ })).not.toBeInTheDocument();
  });
});
