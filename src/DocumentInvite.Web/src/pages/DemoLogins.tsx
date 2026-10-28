import { secondaryButtonClass } from '../components/Field';
import { useDemoConfig } from './demo';

/** The demo logins on the sign-in page. Renders nothing when demo mode is off. */
export function DemoLogins({ onUse }: { onUse: (email: string, password: string) => void }) {
  const demo = useDemoConfig();
  if (!demo.enabled || demo.logins.length === 0) return null;

  return (
    <section
      aria-label="Demo logins"
      className="w-full max-w-sm space-y-3 rounded-card border border-brand-200 bg-brand-50 p-4 text-sm"
    >
      <h2 className="font-semibold text-ink">Demo logins</h2>
      <ul className="space-y-3">
        {demo.logins.map((login) => (
          <li key={login.role} className="flex items-center justify-between gap-3">
            <div className="min-w-0">
              <p className="font-medium text-ink">{login.role}</p>
              <p className="break-all text-ink-muted">{login.email}</p>
              <p className="break-all text-ink-muted">{login.password}</p>
            </div>
            <button
              type="button"
              className={secondaryButtonClass}
              aria-label={`Use the ${login.role} login`}
              onClick={() => onUse(login.email, login.password)}
            >
              Use
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
