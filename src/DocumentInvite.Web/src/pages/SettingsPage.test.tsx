import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, mockApi, meBody, renderApp, resetSession, tokens } from '../test-utils';
import { SettingsPage } from './SettingsPage';

afterEach(resetSession);

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Admin'])),
  };
}

describe('SettingsPage', () => {
  it('loads and saves the settings', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
      'PUT /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 3, retentionDaysAfterCompletion: 365 }),
    });
    renderApp(<SettingsPage />);
    const user = userEvent.setup();

    const reminder = await screen.findByLabelText('Default reminder interval (days)');
    expect(reminder).toHaveValue(7);
    await user.clear(reminder);
    await user.type(reminder, '3');
    await user.click(screen.getByRole('button', { name: 'Save settings' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Settings saved.');
    const put = calls.find((c) => c.key === 'PUT /api/settings');
    expect(JSON.parse(put!.init!.body as string)).toEqual({
      defaultReminderIntervalDays: 3,
      retentionDaysAfterCompletion: 365,
    });
  });

  it('shows validation errors from the API', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
      'PUT /api/settings': () =>
        jsonResponse(400, { title: 'Validation', errors: { retentionDaysAfterCompletion: ['Too small.'] } }),
    });
    renderApp(<SettingsPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Save settings' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Too small.');
  });

  it('runs the reminder job on request and shows the counts', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
      'POST /api/jobs/reminders/run': () =>
        jsonResponse(200, {
          windowKey: 'manual-1',
          alreadyRan: false,
          invitesDue: 3,
          remindersSent: 2,
          overdueNoticesSent: 1,
          failed: 0,
        }),
    });
    renderApp(<SettingsPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Run reminders now' }));

    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent('2 reminders sent');
    expect(status).toHaveTextContent('1 overdue notice sent');
    expect(status).toHaveTextContent('0 failed');
    expect(calls.filter((c) => c.key === 'POST /api/jobs/reminders/run')).toHaveLength(1);
  });

  it('shows an error when the reminder job fails', async () => {
    mockApi({
      ...signedIn(),
      'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
      'POST /api/jobs/reminders/run': () => jsonResponse(500, { title: 'Server error' }),
    });
    renderApp(<SettingsPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Run reminders now' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('previews the retention purge without deleting, then runs it after confirmation', async () => {
    const preview = {
      retentionDays: 365,
      cutoff: '2025-01-01T00:00:00Z',
      requestCount: 1,
      fileCount: 2,
      contactCount: 1,
      requests: {
        items: [{ id: 'l1', reference: 'LN-9', name: 'Old request', completedAt: '2024-06-01T00:00:00Z', fileCount: 2 }],
        total: 1,
        page: 1,
        pageSize: 25,
      },
    };
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
      'GET /api/gdpr/retention/preview?page=1&pageSize=25': () => jsonResponse(200, preview),
      'POST /api/gdpr/retention/run': () =>
        jsonResponse(200, { requestsDeleted: 1, filesDeleted: 2, contactsPseudonymised: 1 }),
    });
    renderApp(<SettingsPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Preview purge' }));
    expect(await screen.findByText(/LN-9 - Old request/)).toBeInTheDocument();
    expect(screen.getByText(/1 request, 2 files and 1 contact would be purged/)).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'POST /api/gdpr/retention/run')).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Run purge...' }));
    await user.click(screen.getByRole('button', { name: 'Yes, purge' }));

    expect(await screen.findByText(/Purge finished/)).toHaveTextContent('1 request and 2 files deleted, 1 contact pseudonymised');
  });
});
