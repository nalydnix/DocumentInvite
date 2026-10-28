import { Navigate } from 'react-router-dom';
import { hasAnyRole, useAuth } from './AuthContext';

/** The landing route: sends a signed-in user to Loans, or to the audit log when Loans is not open to them. */
export function HomeRedirect() {
  const { state } = useAuth();
  const canSeeLoans = state.status === 'authenticated' && hasAnyRole(state.user, ['Admin', 'LoanOfficer']);
  return <Navigate to={canSeeLoans ? '/loans' : '/audit'} replace />;
}
