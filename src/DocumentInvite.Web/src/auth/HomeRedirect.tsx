import { Navigate } from 'react-router-dom';
import { hasAnyRole, useAuth } from './AuthContext';

/** The landing route: sends a signed-in user to Requests, or to the audit log when Requests is not open to them. */
export function HomeRedirect() {
  const { state } = useAuth();
  const canSeeRequests = state.status === 'authenticated' && hasAnyRole(state.user, ['Admin', 'Requester']);
  return <Navigate to={canSeeRequests ? '/requests' : '/audit'} replace />;
}
