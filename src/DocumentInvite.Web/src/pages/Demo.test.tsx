import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { jsonResponse, meBody, mockApi, renderApp, resetSession, tokens } from '../test-utils';
import { HomePage } from './HomePage';
import { LoginPage } from './LoginPage';
import { SettingsPage } from './SettingsPage';

afterEach(() => {
  resetSession();
  localStorage.clear();
});

const demoOn = {
  enabled: true,
  logins: [
    { role: 'Admin', email: 'admin@demo.test', password: 'Demo-Admin-Passw0rd!' },
    { role: 'LoanOfficer', email: 'officer@demo.test', password: 'Demo-Officer-Passw0rd!' },
    { role: 'Auditor', email: 'auditor@demo.test', password: 'Demo-Auditor-Passw0rd!' },
  ],
};

function signedIn() {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody(['Admin'])),
  };
}

describe('demo logins on the sign-in page', () => {
  it('shows every demo login and fills the form from one', async () => {
    mockApi({ 'GET /api/demo/config': () => jsonResponse(200, demoOn) });
    renderApp(<LoginPage />, '/login');

    const panel = await screen.findByRole('region', { name: 'Demo logins' });
    expect(panel).toHaveTextContent('admin@demo.test');
    expect(panel).toHaveTextContent('officer@demo.test');
    expect(panel).toHaveTextContent('auditor@demo.test');

    await userEvent.click(screen.getByRole('button', { name: 'Use the Auditor login' }));

    expect(screen.getByLabelText('Email')).toHaveValue('auditor@demo.test');
    expect(screen.getByLabelText('Password')).toHaveValue('Demo-Auditor-Passw0rd!');
  });

  it('shows nothing when demo mode is off', async () => {
    const { fetchMock } = mockApi({
      'GET /api/demo/config': () => jsonResponse(200, { enabled: false, logins: [] }),
    });
    renderApp(<LoginPage />, '/login');

    await screen.findByRole('button', { name: 'Sign in' });
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(screen.queryByRole('region', { name: 'Demo logins' })).not.toBeInTheDocument();
  });
});

describe('How to try this panel', () => {
  it('lists the walkthrough steps in demo mode and stays dismissed', async () => {
    mockApi({ ...signedIn(), 'GET /api/demo/config': () => jsonResponse(200, demoOn) });
    const view = renderApp(<HomePage />);

    const panel = await screen.findByRole('region', { name: 'How to try this' });
    const steps = panel.querySelectorAll('li');
    expect(steps).toHaveLength(5);
    expect(steps[0]).toHaveTextContent(/sign in/i);
    expect(steps[1]).toHaveTextContent(/outbox/i);
    expect(steps[2]).toHaveTextContent(/portal link/i);
    expect(steps[3]).toHaveTextContent(/review/i);
    expect(steps[4]).toHaveTextContent(/audit log/i);

    await userEvent.click(screen.getByRole('button', { name: 'Dismiss how to try this' }));
    expect(screen.queryByRole('region', { name: 'How to try this' })).not.toBeInTheDocument();

    view.unmount();
    renderApp(<HomePage />);
    await screen.findByText(/Secure document collection/);
    expect(screen.queryByRole('region', { name: 'How to try this' })).not.toBeInTheDocument();
  });

  it('is not shown when demo mode is off', async () => {
    mockApi({ ...signedIn(), 'GET /api/demo/config': () => jsonResponse(200, { enabled: false, logins: [] }) });
    renderApp(<HomePage />);

    await screen.findByText(/Secure document collection/);
    expect(screen.queryByRole('region', { name: 'How to try this' })).not.toBeInTheDocument();
  });
});

describe('Reset demo data', () => {
  const settingsRoutes = {
    'GET /api/settings': () => jsonResponse(200, { defaultReminderIntervalDays: 7, retentionDaysAfterCompletion: 365 }),
  };

  it('asks for confirmation, then resets', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      ...settingsRoutes,
      'GET /api/demo/config': () => jsonResponse(200, demoOn),
      'POST /api/demo/reset': () => jsonResponse(200, { loans: 3, contacts: 3 }),
    });
    renderApp(<SettingsPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reset demo data' }));
    expect(calls.some((c) => c.key === 'POST /api/demo/reset')).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Yes, reset' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Demo data reset. 3 sample loans created.');
    expect(calls.filter((c) => c.key === 'POST /api/demo/reset')).toHaveLength(1);
  });

  it('can be cancelled without calling the API', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      ...settingsRoutes,
      'GET /api/demo/config': () => jsonResponse(200, demoOn),
    });
    renderApp(<SettingsPage />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Reset demo data' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('button', { name: 'Reset demo data' })).toBeInTheDocument();
    expect(calls.some((c) => c.key === 'POST /api/demo/reset')).toBe(false);
  });

  it('is hidden when demo mode is off', async () => {
    mockApi({
      ...signedIn(),
      ...settingsRoutes,
      'GET /api/demo/config': () => jsonResponse(200, { enabled: false, logins: [] }),
    });
    renderApp(<SettingsPage />);

    await screen.findByRole('button', { name: 'Run reminders now' });
    expect(screen.queryByRole('button', { name: 'Reset demo data' })).not.toBeInTheDocument();
  });
});
