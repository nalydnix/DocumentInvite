import { screen, within } from '@testing-library/react';
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
};
const older = { ...newer, id: 'e1', subject: 'Documents requested for Loan 42', createdAt: '2027-03-01T10:00:00.000Z', provider: 'Demo', deliveryError: null };
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
    'GET /api/me': () => jsonResponse(200, meBody(['LoanOfficer'])),
  };
}

describe('OutboxPage', () => {
  it('lists emails in the order the API returns them and flags failed deliveries', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?skip=0&take=50': () => jsonResponse(200, { items: [newer, older], total: 2 }) });

    renderApp(<OutboxPage />);

    const list = await screen.findByRole('list', { name: 'Emails' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('Reminder: 2 items still needed');
    expect(rows[0]).toHaveTextContent('Not delivered');
    expect(rows[1]).toHaveTextContent('Documents requested for Loan 42');
    expect(rows[1]).not.toHaveTextContent('Not delivered');
  });

  it('renders the selected email in a sandboxed iframe whose links open in a new tab', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/outbox?skip=0&take=50': () => jsonResponse(200, { items: [older], total: 1 }),
      'GET /api/outbox/e1': () => jsonResponse(200, detail),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Documents requested for Loan 42/ }));

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
      'GET /api/outbox?skip=0&take=50': () => jsonResponse(200, { items: [older], total: 1 }),
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
      'GET /api/outbox?skip=0&take=50': () => jsonResponse(200, { items: [newer], total: 1 }),
      'GET /api/outbox/e2': () => jsonResponse(200, { ...newer, providerMessageId: null, htmlBody: '<p>x</p>', textBody: 'x' }),
    });
    renderApp(<OutboxPage />);

    await userEvent.click(await screen.findByRole('button', { name: /Reminder/ }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Resend rejected the email (HTTP 422).');
  });

  it('shows an empty state', async () => {
    mockApi({ ...signedIn(), 'GET /api/outbox?skip=0&take=50': () => jsonResponse(200, { items: [], total: 0 }) });

    renderApp(<OutboxPage />);

    expect(await screen.findByText('No emails yet')).toBeInTheDocument();
  });

  it('prefixes the html with a base tag', () => {
    expect(framedDocument('<p>x</p>')).toBe('<base target="_blank"><p>x</p>');
  });
});
