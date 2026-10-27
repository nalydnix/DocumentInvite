import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { AuditPage, type AuditEvent } from './AuditPage';

afterEach(resetSession);

const event: AuditEvent = {
  id: 7,
  occurredAt: '2026-03-01T10:00:00.000Z',
  actorType: 'User',
  actorId: '11111111-1111-1111-1111-111111111111',
  action: 'contact.created',
  targetType: 'contact',
  targetId: '22222222-2222-2222-2222-222222222222',
  ipAddress: '10.0.0.1',
  userAgent: null,
  correlationId: 'abc',
  detailsJson: '{"partyType":"Borrower"}',
};

const page = (items: AuditEvent[], total = items.length, pageNo = 1) => ({ items, total, page: pageNo, pageSize: 25 });

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Auditor'])),
  };
}

describe('AuditPage', () => {
  it('lists events', async () => {
    mockApi({ ...signedIn(), 'GET /api/audit?page=1&pageSize=25': () => jsonResponse(200, page([event])) });

    renderApp(<AuditPage />);

    const list = await screen.findByRole('list', { name: 'Audit events' });
    expect(within(list).getByText('contact.created')).toBeInTheDocument();
    expect(within(list).getByText(/partyType/)).toBeInTheDocument();
  });

  it('applies filters and goes back to the first page', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/audit?page=1&pageSize=25': () => jsonResponse(200, page([event], 60)),
      'GET /api/audit?page=2&pageSize=25': () => jsonResponse(200, page([event], 60, 2)),
      'GET /api/audit?action=user.created&targetType=user&page=1&pageSize=25': () => jsonResponse(200, page([])),
    });
    renderApp(<AuditPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 2 of 3')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Action'), 'user.created');
    await user.type(screen.getByLabelText('Target type'), 'user');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));

    expect(await screen.findByText('No events match the filters.')).toBeInTheDocument();
    expect(calls.some((c) => c.key.includes('action=user.created'))).toBe(true);
  });

  it('shows a valid verification result', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/audit?page=1&pageSize=25': () => jsonResponse(200, page([])),
      'POST /api/audit/verify': () => jsonResponse(200, { isValid: true, firstBrokenEventId: null, eventsChecked: 12 }),
    });
    renderApp(<AuditPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));

    expect(await screen.findByRole('status')).toHaveTextContent('valid (12 events checked)');
  });

  it('shows the first broken event', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/audit?page=1&pageSize=25': () => jsonResponse(200, page([])),
      'POST /api/audit/verify': () => jsonResponse(200, { isValid: false, firstBrokenEventId: 5, eventsChecked: 5 }),
    });
    renderApp(<AuditPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));

    expect(await screen.findByRole('status')).toHaveTextContent('NOT valid. The first broken event is #5');
  });

  it('exports the current filter as CSV', async () => {
    const createObjectURL = vi.fn(() => 'blob:x');
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() }));
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/audit?page=1&pageSize=25': () => jsonResponse(200, page([])),
      'GET /api/audit/export.csv?action=user': () => new Response('id\r\n', { status: 200 }),
    });
    renderApp(<AuditPage />);
    const user = userEvent.setup();
    // Applying a filter first: the export must use it too.
    await user.type(await screen.findByLabelText('Action'), 'user');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));

    await user.click(screen.getByRole('button', { name: 'Export CSV' }));

    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
    expect(calls.some((c) => c.key === 'GET /api/audit/export.csv?action=user')).toBe(true);
  });
});
