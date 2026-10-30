import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { jsonResponse, mockApi, renderApp } from '../test-utils';
import { clearPortalSession, type PortalInvite, type PortalItem } from './portalApi';
import { PortalPage } from './PortalPage';

afterEach(() => {
  clearPortalSession();
  vi.unstubAllGlobals();
});

function item(overrides: Partial<PortalItem>): PortalItem {
  return {
    id: 'i1',
    name: 'Tax return',
    kind: 'FileUpload',
    instructions: 'Last two years',
    allowMultiple: false,
    fieldType: null,
    isRequired: true,
    status: 'Outstanding',
    rejectionReason: null,
    fieldValue: null,
    note: null,
    files: [],
    ...overrides,
  };
}

function invite(overrides: Partial<PortalInvite> = {}): PortalInvite {
  return {
    requestName: 'Acme acquisition',
    partyFirstName: 'Ann',
    dueDate: '2027-02-01',
    status: 'InProgress',
    privacyAccepted: true,
    itemCount: 3,
    completedItemCount: 1,
    items: [
      item({ id: 'i1', status: 'Submitted', files: [{ id: 'f1', fileName: 'tax.pdf', sizeBytes: 2048, uploadedAt: null }] }),
      item({ id: 'i2', name: 'Bank statements', status: 'Rejected', rejectionReason: 'Page 2 is cut off', allowMultiple: true, instructions: 'Six months' }),
      item({ id: 'i3', name: 'Years in business', kind: 'FormField', fieldType: 'Number', instructions: '' }),
    ],
    ...overrides,
  };
}

const session = () => jsonResponse(200, { sessionToken: 'session-1', expiresAt: '2027-01-01T00:00:00Z' });

function renderPortal(route = '/p/link-token') {
  return renderApp(
    <Routes>
      <Route path="/p/:token" element={<PortalPage />} />
    </Routes>,
    route,
  );
}

describe('PortalPage', () => {
  it('exchanges the link token and shows the checklist with progress, due date and rejection reason', async () => {
    const { calls } = mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
    });

    renderPortal();

    expect(await screen.findByRole('heading', { name: 'Hello Ann' })).toBeInTheDocument();
    expect(screen.getByText(/Acme acquisition/)).toBeInTheDocument();
    expect(screen.getByText('February 1, 2027')).toBeInTheDocument();

    const bar = screen.getByRole('progressbar', { name: 'Request progress' });
    expect(bar).toHaveAttribute('aria-valuenow', '33');
    expect(screen.getByText('1 of 3 items done')).toBeInTheDocument();

    const list = screen.getByRole('list', { name: 'Checklist' });
    const rejected = within(list).getByRole('listitem', { name: 'Bank statements' });
    expect(within(rejected).getByRole('alert')).toHaveTextContent('Page 2 is cut off');
    expect(within(rejected).getByText('Needs attention')).toBeInTheDocument();
    expect(screen.getByText('tax.pdf')).toBeInTheDocument();
    expect(screen.getByText('Last two years')).toBeInTheDocument();

    expect(JSON.parse(calls[0].init!.body as string)).toEqual({ token: 'link-token', password: null });
    expect(calls[1].init!.headers).toMatchObject({ Authorization: 'Bearer session-1' });
  });

  it('shows the message thread with the requester below the checklist', async () => {
    mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
      'GET /api/portal/messages?page=1&pageSize=10': () =>
        jsonResponse(200, {
          items: [{ id: 'm1', authorType: 'Team', body: 'Please resend page 2', createdAt: '2027-01-05T10:00:00Z' }],
          total: 1,
          page: 1,
          pageSize: 10,
        }),
    });

    renderPortal();

    const thread = await screen.findByRole('region', { name: 'Messages' });
    expect(await within(thread).findByText('Please resend page 2')).toBeInTheDocument();
    expect(within(thread).getByLabelText('Message your requester')).toBeInTheDocument();
  });

  it('asks the party to accept the privacy notice before anything can be added', async () => {
    let accepted = false;
    const { calls } = mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite({ privacyAccepted: accepted })),
      'POST /api/portal/privacy': () => {
        accepted = true;
        return new Response(null, { status: 204 });
      },
    });

    renderPortal();

    const accept = await screen.findByRole('button', { name: 'Accept and continue' });
    expect(accept).toBeDisabled();
    const card = screen.getByRole('listitem', { name: 'Bank statements' });
    expect(within(card).getByLabelText(/Choose files/)).toBeDisabled();
    expect(screen.getByRole('button', { name: /Save answer/ })).toBeDisabled();

    await userEvent.click(screen.getByLabelText('I have read and accept the privacy notice'));
    await userEvent.click(accept);

    expect(await screen.findByRole('heading', { name: 'Hello Ann' })).toBeInTheDocument();
    await vi.waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Accept and continue' })).not.toBeInTheDocument(),
    );
    expect(within(screen.getByRole('listitem', { name: 'Bank statements' })).getByLabelText(/Choose files/)).toBeEnabled();
    expect(calls.some((c) => c.key === 'POST /api/portal/privacy')).toBe(true);
  });

  it('saves a form answer', async () => {
    const { calls } = mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
      'PUT /api/portal/items/i3/field': () => jsonResponse(200, item({ id: 'i3', status: 'Submitted', fieldValue: '12' })),
    });

    renderPortal();

    const input = await screen.findByLabelText('Answer for Years in business');
    expect(input).toHaveAttribute('type', 'number');
    await userEvent.type(input, '12');
    await userEvent.click(screen.getByRole('button', { name: /Save answer/ }));

    expect(await screen.findByText('Saved')).toBeInTheDocument();
    const put = calls.find((c) => c.key === 'PUT /api/portal/items/i3/field')!;
    expect(JSON.parse(put.init!.body as string)).toEqual({ value: '12' });
  });

  it('shows a validation message from the server for a bad answer', async () => {
    mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
      'PUT /api/portal/items/i3/field': () => jsonResponse(400, { title: 'Invalid', errors: { value: ['Enter a number.'] } }),
    });

    renderPortal();
    await userEvent.type(await screen.findByLabelText('Answer for Years in business'), '1');
    await userEvent.click(screen.getByRole('button', { name: /Save answer/ }));

    expect(await screen.findByText('Enter a number.')).toBeInTheDocument();
  });

  it('saves a note on an item', async () => {
    const { calls } = mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
      'PUT /api/portal/items/i1/note': () => jsonResponse(200, item({ note: 'Posted too' })),
    });

    renderPortal();
    await screen.findByRole('heading', { name: 'Hello Ann' });
    await userEvent.click(await screen.findByRole('button', { name: /Add a note to Tax return/ }));
    await userEvent.type(screen.getByLabelText('Note for Tax return'), 'Posted too');
    await userEvent.click(screen.getByRole('button', { name: 'Save note' }));

    expect(await screen.findByText('Note saved')).toBeInTheDocument();
    const put = calls.find((c) => c.key === 'PUT /api/portal/items/i1/note')!;
    expect(JSON.parse(put.init!.body as string)).toEqual({ note: 'Posted too' });
  });

  it.each([
    ['link_not_found', 404, 'This link is not valid'],
    ['link_expired', 410, 'This link has expired'],
    ['link_revoked', 410, 'This link is no longer active'],
  ])('shows a friendly error for %s', async (code, status, title) => {
    mockApi({
      'POST /api/portal/session': () => jsonResponse(status, { title, detail: 'Ask your requester for a new link.', code }),
    });

    renderPortal();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('heading', { name: title })).toBeInTheDocument();
    expect(alert).toHaveTextContent('Ask your requester for a new link.');
  });

  it('asks for the password and retries after a wrong one', async () => {
    const { calls } = mockApi({
      'POST /api/portal/session': (_url, init) => {
        const body = JSON.parse(init!.body as string) as { password: string | null };
        if (!body.password) {
          return jsonResponse(401, { title: 'Password required', detail: 'Enter the password.', code: 'password_required' });
        }
        if (body.password !== 'right-password') {
          return jsonResponse(401, { title: 'Incorrect', detail: 'That password is not correct. Try again.', code: 'invalid_password' });
        }
        return session();
      },
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
    });

    renderPortal();

    await userEvent.type(await screen.findByLabelText('Password'), 'wrong-password');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('That password is not correct. Try again.')).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText('Password'));
    await userEvent.type(screen.getByLabelText('Password'), 'right-password');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('heading', { name: 'Hello Ann' })).toBeInTheDocument();
    expect(calls.filter((c) => c.key === 'POST /api/portal/session')).toHaveLength(3);
  });

  it('asks the browser not to send a referrer while the portal is shown', async () => {
    mockApi({
      'POST /api/portal/session': session,
      'GET /api/portal/invite': () => jsonResponse(200, invite()),
    });

    const view = renderPortal();
    await screen.findByRole('heading', { name: 'Hello Ann' });
    expect(document.head.querySelector('meta[name="referrer"]')).toHaveAttribute('content', 'no-referrer');

    view.unmount();
    expect(document.head.querySelector('meta[name="referrer"]')).toBeNull();
  });
});
