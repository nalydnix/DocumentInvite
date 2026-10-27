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
});
