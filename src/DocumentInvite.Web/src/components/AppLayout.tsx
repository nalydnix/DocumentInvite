import { useState } from 'react';
import { Link, NavLink, Outlet } from 'react-router-dom';
import { hasAnyRole, useAuth, type CurrentUser, type Role } from '../auth/AuthContext';
import { useUnreadOutboxCount } from '../lib/outbox';
import { secondaryButtonClass } from './Field';

interface NavItem {
  to: string;
  label: string;
  /** Roles that see the link. Omitted means every signed-in user. */
  roles?: readonly Role[];
}

export const navItems: NavItem[] = [
  { to: '/requests', label: 'Requests', roles: ['Admin', 'Requester'] },
  { to: '/templates', label: 'Templates', roles: ['Admin', 'Requester'] },
  { to: '/outbox', label: 'Outbox', roles: ['Admin', 'Requester'] },
  { to: '/contacts', label: 'Contacts', roles: ['Admin', 'Requester'] },
  { to: '/users', label: 'Users', roles: ['Admin'] },
  { to: '/audit', label: 'Audit log', roles: ['Admin', 'Auditor'] },
  { to: '/settings', label: 'Settings', roles: ['Admin'] },
];

function UnreadBadge({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <span className="ml-2 inline-flex min-w-5 items-center justify-center rounded-full bg-brand-600 px-1.5 text-xs font-semibold text-white">
      {count > 99 ? '99+' : count}
      <span className="sr-only"> unread</span>
    </span>
  );
}

function NavLinks({ user, onNavigate }: { user: CurrentUser; onNavigate?: () => void }) {
  const outboxUnread = useUnreadOutboxCount(hasAnyRole(user, ['Admin', 'Requester']));
  return (
    <nav aria-label="Main" className="flex flex-col gap-1">
      {navItems
        .filter((item) => hasAnyRole(user, item.roles))
        .map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            onClick={onNavigate}
            className={({ isActive }) =>
              `rounded-lg px-3 py-2 text-sm font-medium ${
                isActive ? 'bg-brand-50 text-brand-700' : 'text-ink hover:bg-surface-muted'
              }`
            }
          >
            {item.label}
            {item.to === '/outbox' && <UnreadBadge count={outboxUnread} />}
          </NavLink>
        ))}
    </nav>
  );
}

function UserPanel({ user, onSignOut }: { user: CurrentUser; onSignOut: () => void }) {
  return (
    <div className="space-y-2 text-sm">
      <div>
        <p className="font-medium text-ink">{user.displayName}</p>
        <p className="text-ink-muted">{user.roles.join(', ')}</p>
      </div>
      <button type="button" onClick={onSignOut} className={`${secondaryButtonClass} w-full`}>
        Sign out
      </button>
    </div>
  );
}

/** Responsive shell: a sidebar on desktop and a menu behind a button on mobile. */
export function AppLayout() {
  const { state, signOut } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  if (state.status !== 'authenticated') return <Outlet />;
  const { user } = state;

  return (
    <div className="flex min-h-screen flex-col md:flex-row">
      <aside className="hidden w-60 shrink-0 flex-col justify-between border-r border-surface-border bg-surface p-4 md:flex">
        <div className="space-y-6">
          <Link to="/" className="block text-lg font-semibold text-brand-600">
            DocumentInvite
          </Link>
          <NavLinks user={user} />
        </div>
        <UserPanel user={user} onSignOut={signOut} />
      </aside>

      <header className="border-b border-surface-border bg-surface md:hidden">
        <div className="flex items-center justify-between px-4 py-3">
          <Link to="/" className="text-lg font-semibold text-brand-600">
            DocumentInvite
          </Link>
          <button
            type="button"
            aria-expanded={menuOpen}
            aria-controls="mobile-menu"
            onClick={() => setMenuOpen((open) => !open)}
            className={secondaryButtonClass}
          >
            {menuOpen ? 'Close' : 'Menu'}
          </button>
        </div>
        {menuOpen && (
          <div id="mobile-menu" className="space-y-4 border-t border-surface-border px-4 py-3">
            <NavLinks user={user} onNavigate={() => setMenuOpen(false)} />
            <UserPanel user={user} onSignOut={signOut} />
          </div>
        )}
      </header>

      <main className="mx-auto w-full max-w-5xl flex-1 px-4 py-6">
        <Outlet />
      </main>
    </div>
  );
}
