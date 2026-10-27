import { useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';

export function HomePage() {
  const { state } = useAuth();
  const name = state.status === 'authenticated' ? state.user.displayName : '';
  return <Card title={`Welcome${name ? `, ${name}` : ''}`}>Secure document collection for lenders.</Card>;
}
