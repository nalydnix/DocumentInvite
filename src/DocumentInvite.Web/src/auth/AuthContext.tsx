import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch, clearSession, hasStoredSession, onSessionEnded, refreshSession, signIn } from '../lib/api';

export const ROLES = ['Admin', 'Requester', 'Auditor'] as const;
export type Role = (typeof ROLES)[number];

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  roles: Role[];
  accessExpiresAt: string | null;
}

type AuthState =
  | { status: 'loading' }
  | { status: 'anonymous' }
  | { status: 'authenticated'; user: CurrentUser };

interface AuthContextValue {
  state: AuthState;
  signIn: (email: string, password: string) => Promise<void>;
  signOut: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<AuthState>(() =>
    hasStoredSession() ? { status: 'loading' } : { status: 'anonymous' },
  );

  // After a reload the access token is gone; restore the session from the refresh token.
  useEffect(() => {
    if (!hasStoredSession()) return;
    let cancelled = false;
    void (async () => {
      let next: AuthState = { status: 'anonymous' };
      if (await refreshSession()) {
        try {
          next = { status: 'authenticated', user: await apiFetch<CurrentUser>('/api/me') };
        } catch {
          clearSession();
        }
      }
      if (!cancelled) setState(next);
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(
    () =>
      onSessionEnded(() => {
        queryClient.clear();
        setState({ status: 'anonymous' });
      }),
    [queryClient],
  );

  const handleSignIn = useCallback(async (email: string, password: string) => {
    await signIn(email, password);
    try {
      setState({ status: 'authenticated', user: await apiFetch<CurrentUser>('/api/me') });
    } catch (error) {
      clearSession();
      throw error;
    }
  }, []);

  const handleSignOut = useCallback(() => {
    clearSession();
    queryClient.clear();
    setState({ status: 'anonymous' });
  }, [queryClient]);

  const value = useMemo(
    () => ({ state, signIn: handleSignIn, signOut: handleSignOut }),
    [state, handleSignIn, handleSignOut],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside an AuthProvider.');
  return value;
}

/** True when the user holds at least one of the roles (or no roles are required). */
export function hasAnyRole(user: CurrentUser, roles?: readonly Role[]): boolean {
  return !roles || roles.length === 0 || roles.some((role) => user.roles.includes(role));
}
