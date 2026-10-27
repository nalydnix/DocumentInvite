import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ROLES, type Role } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { ErrorList, Field, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';

export interface UserSummary {
  id: string;
  email: string;
  displayName: string;
  roles: Role[];
  accessExpiresAt: string | null;
  isLockedOut: boolean;
}

/** The expiry is the end of the chosen day in the viewer's time zone. */
function toExpiry(date: string): string | null {
  return date ? new Date(`${date}T23:59:59`).toISOString() : null;
}

function toDateInput(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function RolePicker({
  value,
  onChange,
  idPrefix,
}: {
  value: Role[];
  onChange: (roles: Role[]) => void;
  idPrefix: string;
}) {
  return (
    <fieldset className="flex flex-wrap gap-x-4 gap-y-1">
      <legend className="sr-only">Roles</legend>
      {ROLES.map((role) => (
        <label key={role} htmlFor={`${idPrefix}-${role}`} className="flex items-center gap-2 text-sm">
          <input
            id={`${idPrefix}-${role}`}
            type="checkbox"
            checked={value.includes(role)}
            onChange={(e) => onChange(e.target.checked ? [...value, role] : value.filter((r) => r !== role))}
          />
          {role}
        </label>
      ))}
    </fieldset>
  );
}

function CreateUserForm() {
  const queryClient = useQueryClient();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [roles, setRoles] = useState<Role[]>([]);
  const [expiry, setExpiry] = useState('');

  const create = useMutation({
    mutationFn: () =>
      apiFetch<UserSummary>('/api/users', {
        method: 'POST',
        body: {
          email,
          displayName,
          password,
          roles,
          accessExpiresAt: roles.includes('Auditor') ? toExpiry(expiry) : null,
        },
      }),
    onSuccess: async () => {
      setEmail('');
      setDisplayName('');
      setPassword('');
      setRoles([]);
      setExpiry('');
      await queryClient.invalidateQueries({ queryKey: ['users'] });
    },
  });

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    create.mutate();
  }

  return (
    <Card title="Create user">
      <form onSubmit={handleSubmit} className="mt-2 grid gap-3 sm:grid-cols-2">
        <Field label="Email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Field
          label="Display name"
          required
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
        />
        <Field
          label="Initial password (at least 12 characters)"
          type="password"
          autoComplete="new-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        {roles.includes('Auditor') && (
          <Field
            label="Access expires on"
            type="date"
            value={expiry}
            onChange={(e) => setExpiry(e.target.value)}
          />
        )}
        <div className="sm:col-span-2">
          <RolePicker value={roles} onChange={setRoles} idPrefix="new" />
        </div>
        <div className="space-y-3 sm:col-span-2">
          <ErrorList messages={messagesOf(create.error)} />
          <button type="submit" disabled={create.isPending} className={primaryButtonClass}>
            Create user
          </button>
        </div>
      </form>
    </Card>
  );
}

function UserRow({ user }: { user: UserSummary }) {
  const queryClient = useQueryClient();
  const [roles, setRoles] = useState<Role[]>(user.roles);
  const [expiry, setExpiry] = useState(toDateInput(user.accessExpiresAt));
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['users'] });

  const saveRoles = useMutation({
    mutationFn: () => apiFetch<UserSummary>(`/api/users/${user.id}/roles`, { method: 'PUT', body: { roles } }),
    onSuccess: refresh,
  });
  const saveExpiry = useMutation({
    mutationFn: (value: string) =>
      apiFetch<UserSummary>(`/api/users/${user.id}/access-expiry`, {
        method: 'PUT',
        body: { accessExpiresAt: toExpiry(value) },
      }),
    onSuccess: refresh,
  });

  const rolesChanged = roles.length !== user.roles.length || roles.some((r) => !user.roles.includes(r));
  const isAuditor = user.roles.includes('Auditor');

  return (
    <li className="space-y-3 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <p className="font-medium text-ink">{user.displayName}</p>
          <p className="text-sm text-ink-muted">{user.email}</p>
        </div>
        {user.isLockedOut && (
          <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs font-medium text-red-700">Locked out</span>
        )}
      </div>

      <RolePicker value={roles} onChange={setRoles} idPrefix={user.id} />
      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          disabled={!rolesChanged || roles.length === 0 || saveRoles.isPending}
          onClick={() => saveRoles.mutate()}
          className={secondaryButtonClass}
          aria-label={`Save roles for ${user.displayName}`}
        >
          Save roles
        </button>
      </div>

      {isAuditor && (
        <div className="flex flex-wrap items-end gap-3">
          <label className="block text-sm font-medium text-ink">
            Access expires on
            <input
              type="date"
              className={inputClass}
              value={expiry}
              onChange={(e) => setExpiry(e.target.value)}
              aria-label={`Access expiry for ${user.displayName}`}
            />
          </label>
          <button
            type="button"
            disabled={saveExpiry.isPending}
            onClick={() => saveExpiry.mutate(expiry)}
            className={secondaryButtonClass}
            aria-label={`Save expiry for ${user.displayName}`}
          >
            Save expiry
          </button>
          {user.accessExpiresAt && (
            <button
              type="button"
              disabled={saveExpiry.isPending}
              onClick={() => {
                setExpiry('');
                saveExpiry.mutate('');
              }}
              className={secondaryButtonClass}
              aria-label={`Clear expiry for ${user.displayName}`}
            >
              Clear expiry
            </button>
          )}
        </div>
      )}
      <ErrorList messages={[...messagesOf(saveRoles.error), ...messagesOf(saveExpiry.error)]} />
    </li>
  );
}

export function UsersPage() {
  const users = useQuery({ queryKey: ['users'], queryFn: () => apiFetch<UserSummary[]>('/api/users') });

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">Users</h1>
      <CreateUserForm />
      {users.isPending && <p className="text-sm text-ink-muted">Loading users...</p>}
      {users.isError && <ErrorList messages={messagesOf(users.error)} />}
      {users.data && (
        <ul className="space-y-3" aria-label="Users">
          {users.data.map((user) => (
            // Keyed on the saved state so the row's editors reset when the server data changes.
            <UserRow
              key={`${user.id}:${user.roles.join(',')}:${user.accessExpiresAt ?? ''}`}
              user={user}
            />
          ))}
        </ul>
      )}
    </div>
  );
}
