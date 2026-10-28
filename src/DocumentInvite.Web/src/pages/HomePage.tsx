import { useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { HowToTryPanel } from './HowToTryPanel';

export function HomePage() {
  const { state } = useAuth();
  const name = state.status === 'authenticated' ? state.user.displayName : '';
  return (
    <div className="space-y-4">
      <HowToTryPanel />
      <Card title={`Welcome${name ? `, ${name}` : ''}`}>Secure document collection for lenders.</Card>
    </div>
  );
}
