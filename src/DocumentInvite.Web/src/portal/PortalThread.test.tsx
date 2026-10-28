import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, renderApp } from '../test-utils';
import { clearPortalSession, type PortalMessage } from './portalApi';
import { PortalThread } from './PortalThread';

afterEach(() => {
  clearPortalSession();
  vi.unstubAllGlobals();
});

const thread: PortalMessage[] = [
  { id: 'm2', authorType: 'Team', body: 'Please send the lease', createdAt: '2027-01-06T10:00:00Z' },
  { id: 'm1', authorType: 'Party', body: 'Which lease?', createdAt: '2027-01-05T10:00:00Z' },
];
const list = (items: PortalMessage[]) => jsonResponse(200, { items, total: items.length, page: 1, pageSize: 10 });
const URL1 = 'GET /api/portal/messages?page=1&pageSize=10';

describe('PortalThread', () => {
  it('shows the thread with the lender and you, never a staff name', async () => {
    mockApi({ [URL1]: () => list(thread) });
    renderApp(<PortalThread privacyAccepted onSessionEnded={() => {}} />);

    const messages = await screen.findByRole('list', { name: 'Messages, newest first' });
    expect(within(messages).getByText('Please send the lease')).toBeInTheDocument();
    expect(within(messages).getByText('Your lender')).toBeInTheDocument();
    expect(within(messages).getByText('You')).toBeInTheDocument();
  });

  it('posts a message and reloads the thread', async () => {
    let posted = '';
    const { calls } = mockApi({
      [URL1]: () => list([]),
      'POST /api/portal/messages': (_url, init) => {
        posted = JSON.parse(String(init?.body)).body;
        return jsonResponse(201, thread[1]);
      },
    });
    renderApp(<PortalThread privacyAccepted onSessionEnded={() => {}} />);
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Message your lender'), 'Hello there');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    await waitFor(() => expect(posted).toBe('Hello there'));
    await waitFor(() => expect(calls.filter((c) => c.key === URL1)).toHaveLength(2));
    expect(screen.getByLabelText('Message your lender')).toHaveValue('');
  });

  it('asks the party to accept the privacy notice before they can write', async () => {
    mockApi({ [URL1]: () => list(thread) });
    renderApp(<PortalThread privacyAccepted={false} onSessionEnded={() => {}} />);

    expect(await screen.findByText('Please send the lease')).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.getByText(/Accept the privacy notice/)).toBeInTheDocument();
  });

  it('tells the page when the session has ended', async () => {
    const onSessionEnded = vi.fn();
    mockApi({ [URL1]: () => jsonResponse(401, {}) });
    renderApp(<PortalThread privacyAccepted onSessionEnded={onSessionEnded} />);

    await waitFor(() => expect(onSessionEnded).toHaveBeenCalled());
  });
});
