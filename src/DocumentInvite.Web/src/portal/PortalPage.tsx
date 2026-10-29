import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { ErrorList, Field, primaryButtonClass } from '../components/Field';
import { ItemCard } from './ItemCard';
import { PortalThread } from './PortalThread';
import {
  acceptPrivacy,
  clearPortalSession,
  getInvite,
  hasSessionFor,
  PortalApiError,
  startSession,
  type PortalInvite,
  type UploadFunction,
} from './portalApi';

/** Keeps the link token out of Referer headers for as long as a portal page is shown. */
function useNoReferrer() {
  useEffect(() => {
    const meta = document.createElement('meta');
    meta.name = 'referrer';
    meta.content = 'no-referrer';
    document.head.appendChild(meta);
    const previousTitle = document.title;
    document.title = 'Your document request';
    return () => {
      meta.remove();
      document.title = previousTitle;
    };
  }, []);
}

type SessionState =
  | { status: 'connecting' }
  | { status: 'password'; error?: string }
  | { status: 'ready' }
  | { status: 'error'; title: string; message: string };

const LINK_TITLES: Record<string, string> = {
  link_not_found: 'This link is not valid',
  link_revoked: 'This link is no longer active',
  link_expired: 'This link has expired',
  rate_limited: 'Too many attempts',
};

function stateForError(error: unknown): SessionState {
  if (error instanceof PortalApiError && error.code === 'password_required') return { status: 'password' };
  if (error instanceof PortalApiError && error.code === 'invalid_password') {
    return { status: 'password', error: error.message };
  }
  if (error instanceof PortalApiError) {
    return {
      status: 'error',
      title: (error.code && LINK_TITLES[error.code]) || 'Something went wrong',
      message: error.message,
    };
  }
  return { status: 'error', title: 'We could not reach the server', message: 'Check your connection and try again.' };
}

/** Opens a portal session for the link: reuses one from this tab, or exchanges the token. */
function usePortalSession(linkToken: string | undefined) {
  const [state, setState] = useState<SessionState>(() =>
    !linkToken
      ? { status: 'error', title: LINK_TITLES.link_not_found, message: 'This link is missing its code.' }
      : hasSessionFor(linkToken)
        ? { status: 'ready' }
        : { status: 'connecting' },
  );
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!linkToken || hasSessionFor(linkToken)) return;
    let cancelled = false;
    void (async () => {
      let next: SessionState;
      try {
        await startSession(linkToken);
        next = { status: 'ready' };
      } catch (error) {
        next = stateForError(error);
      }
      if (!cancelled) setState(next);
    })();
    return () => {
      cancelled = true;
    };
  }, [linkToken, attempt]);

  return {
    state,
    submitPassword: async (password: string) => {
      try {
        await startSession(linkToken!, password);
        setState({ status: 'ready' });
      } catch (error) {
        setState(stateForError(error));
      }
    },
    /** Called when the API says the session ended: forget it and open a new one. */
    restart: () => {
      clearPortalSession();
      setState({ status: 'connecting' });
      setAttempt((n) => n + 1);
    },
  };
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-surface-muted">
      <header className="border-b border-surface-border bg-surface">
        <div className="mx-auto flex max-w-2xl items-center gap-2 px-4 py-3">
          <span className="text-base font-semibold text-brand-700">DocumentInvite</span>
          <span className="text-xs text-ink-muted">Secure document request</span>
        </div>
      </header>
      <main className="mx-auto max-w-2xl space-y-4 px-4 py-6">{children}</main>
    </div>
  );
}

function PasswordForm({ error, onSubmit }: { error?: string; onSubmit: (password: string) => Promise<void> }) {
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    await onSubmit(password);
    setBusy(false);
  }

  return (
    <form onSubmit={submit} className="space-y-4 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <h1 className="text-lg font-semibold text-ink">Enter your password</h1>
      <p className="text-sm text-ink-muted">Your lender protected this request with a password.</p>
      <Field
        label="Password"
        type="password"
        autoComplete="off"
        value={password}
        onChange={(event) => setPassword(event.target.value)}
      />
      <ErrorList messages={error ? [error] : []} />
      <button type="submit" className={`${primaryButtonClass} min-h-11 w-full`} disabled={busy || password === ''}>
        {busy ? 'Checking...' : 'Continue'}
      </button>
    </form>
  );
}

function PrivacyNotice({ onAccepted }: { onAccepted: () => void }) {
  const [agreed, setAgreed] = useState(false);
  const accept = useMutation({ mutationFn: acceptPrivacy, onSuccess: onAccepted });

  return (
    <section
      aria-labelledby="privacy-heading"
      className="space-y-3 rounded-card border border-brand-200 bg-brand-50 p-4"
    >
      <h2 id="privacy-heading" className="text-base font-semibold text-ink">
        Before you start: privacy notice
      </h2>
      <p className="text-sm text-ink">
        Your lender uses this page to collect the documents and information needed for your request. What you upload
        and enter is stored securely, is visible only to the lender&apos;s team, and is kept only as long as needed
        for the request. Each time you open this page or add something is recorded for security. You can ask your
        lender to export or erase your data.
      </p>
      <label className="flex min-h-11 items-start gap-3 text-sm font-medium text-ink">
        <input
          type="checkbox"
          className="mt-1"
          checked={agreed}
          onChange={(event) => setAgreed(event.target.checked)}
        />
        I have read and accept the privacy notice
      </label>
      <ErrorList messages={accept.error instanceof PortalApiError ? accept.error.messages : []} />
      <button
        type="button"
        className={`${primaryButtonClass} min-h-11 w-full sm:w-auto`}
        disabled={!agreed || accept.isPending}
        onClick={() => accept.mutate()}
      >
        {accept.isPending ? 'Saving...' : 'Accept and continue'}
      </button>
    </section>
  );
}

export function ProgressBar({ done, total }: { done: number; total: number }) {
  const percent = total === 0 ? 0 : Math.round((done / total) * 100);
  return (
    <div className="space-y-1">
      <div
        role="progressbar"
        aria-label="Request progress"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        className="h-2 overflow-hidden rounded-full bg-surface-muted"
      >
        <div className="h-full rounded-full bg-brand-600 transition-all" style={{ width: `${percent}%` }} />
      </div>
      <p className="text-sm text-ink-muted">
        {done} of {total} items done
      </p>
    </div>
  );
}

function formatDue(value: string): string {
  return new Date(`${value}T00:00:00Z`).toLocaleDateString('en-US', { dateStyle: 'long', timeZone: 'UTC' });
}

function Checklist({ onSessionEnded, upload }: { onSessionEnded: () => void; upload?: UploadFunction }) {
  const queryClient = useQueryClient();
  const invite = useQuery({
    queryKey: ['portal', 'invite'],
    queryFn: getInvite,
    refetchOnWindowFocus: true,
  });

  useEffect(() => {
    if (invite.error instanceof PortalApiError && invite.error.code === 'session_ended') onSessionEnded();
  }, [invite.error, onSessionEnded]);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: ['portal', 'invite'] });

  if (invite.isPending) return <p className="text-sm text-ink-muted">Loading your request...</p>;
  if (invite.isError) {
    if (invite.error instanceof PortalApiError && invite.error.code === 'session_ended') {
      return <p className="text-sm text-ink-muted">Reconnecting...</p>;
    }
    return <ErrorList messages={invite.error instanceof PortalApiError ? invite.error.messages : ['We could not load your request.']} />;
  }

  const data: PortalInvite = invite.data;
  return (
    <>
      <section className="space-y-3 rounded-card border border-surface-border bg-surface p-4 shadow-card">
        <div>
          <h1 className="break-words text-lg font-semibold text-ink">Hello {data.partyFirstName}</h1>
          <p className="break-words text-sm text-ink-muted">
            {data.requestName} &middot; please finish by <strong className="text-ink">{formatDue(data.dueDate)}</strong>
          </p>
        </div>
        <ProgressBar done={data.completedItemCount} total={data.itemCount} />
      </section>

      {!data.privacyAccepted && <PrivacyNotice onAccepted={refresh} />}

      <ul className="space-y-4" aria-label="Checklist">
        {data.items.map((item) => (
          <ItemCard key={item.id} item={item} privacyAccepted={data.privacyAccepted} onChanged={refresh} upload={upload} />
        ))}
      </ul>

      <PortalThread privacyAccepted={data.privacyAccepted} onSessionEnded={onSessionEnded} />
    </>
  );
}

/** The recipient portal at /p/{token}: no account, just the secure link (and an optional password). */
export function PortalPage({ upload }: { upload?: UploadFunction } = {}) {
  useNoReferrer();
  const { token } = useParams();
  const session = usePortalSession(token);
  const { state, restart } = session;

  return (
    <Shell>
      {state.status === 'connecting' && <p className="text-sm text-ink-muted">Opening your request...</p>}
      {state.status === 'password' && <PasswordForm error={state.error} onSubmit={session.submitPassword} />}
      {state.status === 'error' && (
        <section role="alert" className="space-y-2 rounded-card border border-surface-border bg-surface p-4 shadow-card">
          <h1 className="text-lg font-semibold text-ink">{state.title}</h1>
          <p className="text-sm text-ink-muted">{state.message}</p>
        </section>
      )}
      {state.status === 'ready' && <Checklist onSessionEnded={restart} upload={upload} />}
    </Shell>
  );
}
