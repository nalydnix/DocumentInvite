import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { AppLayout } from '../components/AppLayout';
import { HomeRedirect } from './HomeRedirect';
import { LoginPage } from '../pages/LoginPage';
import { jsonResponse, meBody, mockApi, renderApp, resetSession, tokens } from '../test-utils';
import { ProtectedRoute } from './ProtectedRoute';

afterEach(resetSession);

function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route element={<ProtectedRoute />}>
        <Route element={<AppLayout />}>
          <Route index element={<HomeRedirect />} />
          <Route path="loans" element={<h1>Loans page</h1>} />
          <Route path="audit" element={<h1>Audit page</h1>} />
          <Route element={<ProtectedRoute roles={['Admin']} />}>
            <Route path="users" element={<h1>Users admin</h1>} />
          </Route>
        </Route>
      </Route>
    </Routes>
  );
}

const loginRoutes = (roles: ('Admin' | 'LoanOfficer' | 'Viewer' | 'Auditor')[]) => ({
  'POST /api/auth/login': () => jsonResponse(200, tokens),
  'GET /api/me': () => jsonResponse(200, meBody(roles, 'Dana Admin')),
});

async function signInThroughForm() {
  const user = userEvent.setup();
  await user.type(await screen.findByLabelText('Email'), 'dana@example.com');
  await user.type(screen.getByLabelText('Password'), 'Correct-Horse-12');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('sign-in and protected routes', () => {
  it('sends anonymous visitors to the sign-in page', async () => {
    mockApi({});

    renderApp(<AppRoutes />, '/users');

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('signs in and returns to the page the visitor asked for', async () => {
    mockApi(loginRoutes(['Admin']));

    renderApp(<AppRoutes />, '/users');
    await signInThroughForm();

    expect(await screen.findByRole('heading', { name: 'Users admin' })).toBeInTheDocument();
  });

  it('shows an error and stays on the form when the sign-in is refused', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(401, { title: 'Unauthorized', detail: 'Failed' }) });

    renderApp(<AppRoutes />, '/login');
    await signInThroughForm();

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid email or password.');
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled();
  });

  it('blocks a signed-in user whose role does not allow the page', async () => {
    mockApi(loginRoutes(['Viewer']));

    renderApp(<AppRoutes />, '/users');
    await signInThroughForm();

    expect(await screen.findByRole('heading', { name: 'Not authorised' })).toBeInTheDocument();
  });

  it('restores the session after a reload from the stored refresh token', async () => {
    sessionStorage.setItem('di.refreshToken', 'refresh-1');
    mockApi({
      'POST /api/auth/refresh': () => jsonResponse(200, tokens),
      'GET /api/me': () => jsonResponse(200, meBody(['LoanOfficer'], 'Lee Officer')),
    });

    renderApp(<AppRoutes />, '/');

    expect(await screen.findByRole('heading', { name: 'Loans page' })).toBeInTheDocument();
  });

  it('signs out and returns to the sign-in page', async () => {
    mockApi(loginRoutes(['Admin']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();
    await screen.findByRole('heading', { name: 'Loans page' });

    await userEvent.click(screen.getAllByRole('button', { name: 'Sign out' })[0]);

    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument());
    expect(sessionStorage.getItem('di.refreshToken')).toBeNull();
  });
});

describe('role-aware navigation', () => {
  it('shows the Users link to admins only', async () => {
    mockApi(loginRoutes(['Admin']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();

    const nav = (await screen.findAllByRole('navigation', { name: 'Main' }))[0];

    expect(nav).toHaveTextContent('Users');
  });

  it('hides the Users link from other roles', async () => {
    mockApi(loginRoutes(['Auditor']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();

    const nav = (await screen.findAllByRole('navigation', { name: 'Main' }))[0];

    expect(nav).toHaveTextContent('Audit log');
    expect(nav).not.toHaveTextContent('Users');
  });

  it('opens the menu on mobile', async () => {
    mockApi(loginRoutes(['Admin']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();
    await screen.findByRole('heading', { name: 'Loans page' });

    await userEvent.click(screen.getByRole('button', { name: 'Menu' }));

    expect(screen.getByRole('button', { name: 'Close' })).toHaveAttribute('aria-expanded', 'true');
    expect(document.getElementById('mobile-menu')).toHaveTextContent('Users');
  });
});

describe('landing route', () => {
  it('redirects / to Loans for staff', async () => {
    mockApi(loginRoutes(['LoanOfficer']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();

    expect(await screen.findByRole('heading', { name: 'Loans page' })).toBeInTheDocument();
  });

  it('redirects / to the audit log for an auditor, who cannot open Loans', async () => {
    mockApi(loginRoutes(['Auditor']));
    renderApp(<AppRoutes />, '/');
    await signInThroughForm();

    expect(await screen.findByRole('heading', { name: 'Audit page' })).toBeInTheDocument();
  });
});
