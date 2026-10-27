import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { Card } from '../components/Card';
import { hasAnyRole, useAuth, type Role } from './AuthContext';

/**
 * Renders nested routes only for signed-in users, optionally limited to some roles.
 * Anonymous visitors are sent to the sign-in page and return here afterwards.
 */
export function ProtectedRoute({ roles }: { roles?: readonly Role[] }) {
  const { state } = useAuth();
  const location = useLocation();

  if (state.status === 'loading') {
    return <p className="p-6 text-sm text-ink-muted">Loading...</p>;
  }
  if (state.status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }
  if (!hasAnyRole(state.user, roles)) {
    return (
      <Card title="Not authorised">You do not have access to this page. Ask an administrator if you need it.</Card>
    );
  }
  return <Outlet />;
}
