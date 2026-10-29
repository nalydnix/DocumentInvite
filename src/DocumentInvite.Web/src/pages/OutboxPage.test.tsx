import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, renderApp, resetSession, tokens, meBody } from '../test-utils';
import { framedDocument, OutboxPage } from './OutboxPage';

afterEach(resetSession);

const newer = {
  id: 'e2',
  to: 'pat@example.com',
  subject: 'Reminder: 2 items still needed',
  createdAt: '2027-03-02T10:00:00.000Z',
  provider: 'Resend',
  deliveryError: 'Resend rejected the email (HTTP 422).',
  isRead: true,
};
const older = { ...newer, id: 'e1', subject: 'Documents requested for Request 42', createdAt: '2027-03-01T10:00:00.000Z', provider: 'Demo', deliveryError: null };
const detail = {
  ...older,
  providerMessageId: null,
  htmlBody: '<p>Hello</p><a href="https://app.example.com/p/tok">Open</a>',
  textBody: 'Hello',
};

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Requester'])),
  };
}

describe('OutboxPage', () => {
  it('lists emails in the order the API returns them and flags failed deliveries', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [newer, older], total: 2, page: 1, pageSize: 25 }) });

    renderApp(<OutboxPage />);

    const list = await screen.findByRole('list', { name: 'Emails' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('Reminder: 2 items still needed');
    expect(rows[0]).toHaveTextContent('Not delivered');
    expect(rows[1]).toHaveTextContent('Documents requested for Request 42');
    expect(rows[1]).not.toHaveTextContent('Not delivered');
  });

  it('renders the selected email in a sandboxed iframe whose links open in a new tab', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [older], total: 1, page: 1, pageSize: 25 }),
      'GET /api/outbox/e1': () => jsonResponse(200, detail),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Documents requested for Request 42/ }));

    const frame = (await screen.findByTitle('Email preview')) as HTMLIFrameElement;
    expect(frame.getAttribute('sandbox')).toBe('allow-popups allow-popups-to-escape-sandbox');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-scripts');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-same-origin');
    expect(frame.getAttribute('srcdoc')).toContain('href="https://app.example.com/p/tok"');
    expect(frame.getAttribute('srcdoc')).toContain('<base target="_blank">');
  });

  it('goes back to the list from the viewer', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [older], total: 1, page: 1, pageSize: 25 }),
      'GET /api/outbox/e1': () => jsonResponse(200, detail),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Documents requested/ }));
    await userEvent.click(await screen.findByRole('button', { name: 'Back to outbox' }));

    expect(screen.queryByTitle('Email preview')).not.toBeInTheDocument();
    expect(screen.getByText('Select an email to read it.')).toBeInTheDocument();
  });

  it('shows the delivery error of the selected email', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [newer], total: 1, page: 1, pageSize: 25 }),
      'GET /api/outbox/e2': () => jsonResponse(200, { ...newer, providerMessageId: null, htmlBody: '<p>x</p>', textBody: 'x' }),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Reminder/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Resend rejected the email (HTTP 422).');
  });

  it('shows unread emails distinctly and marks one read when it is opened', async () => {
    const unread = { ...older, id: 'e3', subject: 'Brand new', isRead: false };
    let read = false;
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [{ ...unread, isRead: read }, newer], total: 2, page: 1, pageSize: 25 }),
      'GET /api/outbox/e3': () => jsonResponse(200, { ...detail, ...unread, isRead: read }),
      'POST /api/outbox/e3/read': () => {
        read = true;
        return new Response(null, { status: 204 });
      },
    });
    renderApp(<OutboxPage />);

    const row = (await screen.findByRole('button', { name: /Brand new/ })).closest('li')!;
    expect(row).toHaveTextContent('Unread:');
    expect(within(screen.getByRole('button', { name: /Reminder/ })).queryByText('Unread:', { exact: false })).not.toBeInTheDocument();

    await userEvent.click(within(row).getByRole('button'));

    await waitFor(() => expect(calls.some((c) => c.key === 'POST /api/outbox/e3/read')).toBe(true));
    await waitFor(() => expect(screen.getByRole('button', { name: /Brand new/ })).not.toHaveTextContent('Unread:'));
    expect(await screen.findByRole('button', { name: 'Mark as unread' })).toBeInTheDocument();
  });

  it('does not mark an already read email read again', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [older], total: 1, page: 1, pageSize: 25 }),
      'GET /api/outbox/e1': () => jsonResponse(200, detail),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Documents requested/ }));
    await screen.findByTitle('Email preview');

    expect(calls.some((c) => c.key.startsWith('POST /api/outbox'))).toBe(false);
  });

  it('marks an open email as unread and back to read', async () => {
    let read = true;
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [{ ...older, isRead: read }], total: 1, page: 1, pageSize: 25 }),
      'GET /api/outbox/e1': () => jsonResponse(200, { ...detail, isRead: read }),
      'POST /api/outbox/e1/unread': () => {
        read = false;
        return new Response(null, { status: 204 });
      },
      'POST /api/outbox/e1/read': () => {
        read = true;
        return new Response(null, { status: 204 });
      },
    });
    renderApp(<OutboxPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: /Documents requested/ }));
    await user.click(await screen.findByRole('button', { name: 'Mark as unread' }));
    await user.click(await screen.findByRole('button', { name: 'Mark as read' }));

    expect(await screen.findByRole('button', { name: 'Mark as unread' })).toBeInTheDocument();
    expect(calls.filter((c) => c.key.startsWith('POST /api/outbox')).map((c) => c.key)).toEqual([
      'POST /api/outbox/e1/unread',
      'POST /api/outbox/e1/read',
    ]);
  });

  it('shows an empty state', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [], total: 0, page: 1, pageSize: 25 }) });

    renderApp(<OutboxPage />);

    expect(await screen.findByText('No emails yet')).toBeInTheDocument();
  });

  it('pages through the outbox and keeps the page in the URL', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(200, { items: [newer], total: 26, page: 1, pageSize: 25 }),
      'GET /api/outbox?page=2&pageSize=25': () => jsonResponse(200, { items: [older], total: 26, page: 2, pageSize: 25 }),
    });
    renderApp(<OutboxPage />);
    const user = userEvent.setup();

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument();
    expect(await screen.findByText('Documents requested for Request 42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('opens on the page named in the URL', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/outbox?page=2&pageSize=10': () => jsonResponse(200, { items: [older], total: 11, page: 2, pageSize: 10 }),
    });
    renderApp(<OutboxPage />, '/outbox?page=2&pageSize=10');

    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'GET /api/outbox?page=2&pageSize=10')).toBe(true);
  });

  it('shows an error when the list cannot be loaded', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?page=1&pageSize=25': () => jsonResponse(500, { title: 'Boom' }) });

    renderApp(<OutboxPage />);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('shows a loading state while the list loads', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?page=1&pageSize=25': () => new Promise<Response>(() => {}) });

    renderApp(<OutboxPage />);

    expect(await screen.findByText('Loading...')).toBeInTheDocument();
  });

  it('prefixes the html with a base tag', () => {
    expect(framedDocument('<p>x</p>')).toBe('<base target="_blank"><p>x</p>');
  });
});
