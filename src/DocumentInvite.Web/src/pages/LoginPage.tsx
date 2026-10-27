import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { ErrorList, Field, primaryButtonClass } from '../components/Field';
import { ApiError } from '../lib/api';

export function LoginPage() {
  const { state, signIn } = useAuth();
  const navigate = useNavigate();
  const from = (useLocation().state as { from?: string } | null)?.from ?? '/';
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  if (state.status === 'authenticated') return <Navigate to={from} replace />;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setErrors([]);
    try {
      await signIn(email, password);
      void navigate(from, { replace: true });
    } catch (error) {
      setErrors(error instanceof ApiError ? error.messages : ['Something went wrong. Please try again.']);
      setBusy(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-surface-muted px-4">
      <form
        onSubmit={handleSubmit}
        className="w-full max-w-sm space-y-4 rounded-card border border-surface-border bg-surface p-6 shadow-card"
      >
        <div>
          <h1 className="text-xl font-semibold text-brand-600">DocumentInvite</h1>
          <p className="mt-1 text-sm text-ink-muted">Sign in to your lender account.</p>
        </div>
        <Field
          label="Email"
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
        />
        <Field
          label="Password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <ErrorList messages={errors} />
        <button type="submit" disabled={busy} className={`${primaryButtonClass} w-full`}>
          {busy ? 'Signing in...' : 'Sign in'}
        </button>
      </form>
    </div>
  );
}
