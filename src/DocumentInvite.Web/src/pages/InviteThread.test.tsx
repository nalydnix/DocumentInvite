import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, renderApp, resetSession } from '../test-utils';
import { InviteThread, type StaffMessage } from './InviteThread';

afterEach(resetSession);

const messages: StaffMessage[] = [
  { id: 'm3', authorType: 'Team', authorName: 'Sam Officer', body: 'Secret note', isInternal: true, createdAt: '2027-01-07T10:00:00Z' },
  { id: 'm2', authorType: 'Party', authorName: null, body: 'Where do I upload it?', isInternal: false, createdAt: '2027-01-06T10:00:00Z' },
  { id: 'm1', authorType: 'System', authorName: null, body: 'Tax return was rejected: blurry', isInternal: false, createdAt: '2027-01-05T10:00:00Z' },
];

const page = (items: StaffMessage[], total = items.length) => jsonResponse(200, { items, total, page: 1, pageSize: 10 });
const URL1 = 'GET /api/invites/i1/messages?page=1&pageSize=10';
const OPEN = { name: 'Show messages with Ann Lee' };

describe('InviteThread', () => {
  it('stays closed until opened, then lists the thread with internal notes marked', async () => {
    const { calls } = mockApi({ [URL1]: () => page(messages) });
    renderApp(<InviteThread inviteId="i1" partyName="Ann Lee" canWrite />);
    expect(calls).toHaveLength(0);

    await userEvent.setup().click(screen.getByRole('button', OPEN));

    const list = await screen.findByRole('list', { name: 'Thread with Ann Lee, newest first' });
    expect(within(list).getByText('Secret note')).toBeInTheDocument();
    expect(within(list).getByText('Internal note')).toBeInTheDocument();
    expect(within(list).getByText('Where do I upload it?')).toBeInTheDocument();
    expect(within(list).getByText(/was rejected: blurry/)).toBeInTheDocument();
  });

  it('sends a message to the party or an internal note, and refuses empty text', async () => {
    const posted: string[] = [];
    mockApi({
      [URL1]: () => page([]),
      'POST /api/invites/i1/messages': (_url, init) => {
        posted.push(`message:${JSON.parse(String(init?.body)).body}`);
        return jsonResponse(201, messages[1]);
      },
      'POST /api/invites/i1/notes': (_url, init) => {
        posted.push(`note:${JSON.parse(String(init?.body)).body}`);
        return jsonResponse(201, messages[0]);
      },
    });
    renderApp(<InviteThread inviteId="i1" partyName="Ann Lee" canWrite />);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', OPEN));

    const send = await screen.findByRole('button', { name: 'Send message' });
    expect(send).toBeDisabled();
    await user.type(screen.getByLabelText(/Message to Ann Lee/), '  Please resend  ');
    await user.click(send);
    await waitFor(() => expect(posted).toEqual(['message:Please resend']));

    await user.click(screen.getByLabelText(/Internal note \(team only\)/));
    await user.type(screen.getByLabelText('Internal note'), 'Call her');
    await user.click(screen.getByRole('button', { name: 'Add note' }));
    await waitFor(() => expect(posted).toEqual(['message:Please resend', 'note:Call her']));
  });

  it('keeps what was typed and shows the error when sending fails', async () => {
    mockApi({
      [URL1]: () => page([]),
      'POST /api/invites/i1/messages': () =>
        jsonResponse(409, { title: 'Conflict', detail: 'The link is revoked. Reissue the link before sending a message.' }),
    });
    renderApp(<InviteThread inviteId="i1" partyName="Ann Lee" canWrite />);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', OPEN));

    await user.type(await screen.findByLabelText(/Message to Ann Lee/), 'Hello');
    await user.click(screen.getByRole('button', { name: 'Send message' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('link is revoked');
    expect(screen.getByLabelText(/Message to Ann Lee/)).toHaveValue('Hello');
  });

  it('is read-only for a viewer: no box to type in', async () => {
    mockApi({ [URL1]: () => page([messages[1]]) });
    renderApp(<InviteThread inviteId="i1" partyName="Ann Lee" canWrite={false} />);
    await userEvent.setup().click(screen.getByRole('button', OPEN));

    expect(await screen.findByText('Where do I upload it?')).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.getByText(/read this thread but not post/)).toBeInTheDocument();
  });

  it('pages through a long thread', async () => {
    const older = { ...messages[1], id: 'old', body: 'An older message' };
    const { calls } = mockApi({
      [URL1]: () => page([messages[1]], 11),
      'GET /api/invites/i1/messages?page=2&pageSize=10': () =>
        jsonResponse(200, { items: [older], total: 11, page: 2, pageSize: 10 }),
    });
    renderApp(<InviteThread inviteId="i1" partyName="Ann Lee" canWrite={false} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', OPEN));

    await user.click(await screen.findByRole('button', { name: 'Next' }));

    expect(await screen.findByText('An older message')).toBeInTheDocument();
    expect(calls.map((c) => c.key)).toContain('GET /api/invites/i1/messages?page=2&pageSize=10');
  });
});
