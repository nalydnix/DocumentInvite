import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Card } from '../components/Card';
import { ErrorList, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';

export interface OutboxEmailSummary {
  id: string;
  to: string;
  subject: string;
  createdAt: string;
  provider: 'Demo' | 'Resend';
  deliveryError: string | null;
}

export interface OutboxEmailDetail extends OutboxEmailSummary {
  providerMessageId: string | null;
  htmlBody: string;
  textBody: string;
}

interface OutboxPageResponse {
  items: OutboxEmailSummary[];
  total: number;
}

const PAGE_SIZE = 50;

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

/**
 * Wraps the stored HTML so that its links open in a new tab. The iframe is sandboxed without
 * scripts or same-origin access, so the email cannot touch the app; it may only open links.
 */
export function framedDocument(html: string): string {
  return `<base target="_blank">${html}`;
}

function EmailViewer({ id, onBack }: { id: string; onBack: () => void }) {
  const query = useQuery({ queryKey: ['outbox', id], queryFn: () => apiFetch<OutboxEmailDetail>(`/api/outbox/${id}`) });
  const email = query.data;

  return (
    <section aria-label="Email" className="space-y-3">
      <button type="button" onClick={onBack} className={`${secondaryButtonClass} md:hidden`}>
        Back to outbox
      </button>
      <ErrorList messages={messagesOf(query.error)} />
      {query.isPending && <p className="text-sm text-ink-muted">Loading email...</p>}
      {email && (
        <>
          <div className="rounded-card border border-surface-border bg-surface p-4 text-sm shadow-card">
            <h2 className="break-words text-base font-semibold text-ink">{email.subject}</h2>
            <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-ink-muted">
              <dt>To</dt>
              <dd className="break-all">{email.to}</dd>
              <dt>Sent</dt>
              <dd>{new Date(email.createdAt).toLocaleString()}</dd>
              <dt>Provider</dt>
              <dd>{email.provider}</dd>
            </dl>
            {email.deliveryError && (
              <p role="alert" className="mt-3 rounded-lg border border-red-200 bg-red-50 p-2 text-red-700">
                Not delivered: {email.deliveryError}
              </p>
            )}
          </div>
          <iframe
            title="Email preview"
            sandbox="allow-popups allow-popups-to-escape-sandbox"
            srcDoc={framedDocument(email.htmlBody)}
            className="h-[28rem] w-full rounded-card border border-surface-border bg-white"
          />
        </>
      )}
    </section>
  );
}

export function OutboxPage() {
  const [limit, setLimit] = useState(PAGE_SIZE);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const query = useQuery({
    queryKey: ['outbox', 'list', limit],
    queryFn: () => apiFetch<OutboxPageResponse>(`/api/outbox?skip=0&take=${limit}`),
  });
  const items = query.data?.items ?? [];

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold text-ink">Outbox</h1>
      <p className="text-sm text-ink-muted">Every email the system sends is kept here, newest first.</p>
      <ErrorList messages={messagesOf(query.error)} />
      {query.isPending && <p className="text-sm text-ink-muted">Loading...</p>}
      {query.data && items.length === 0 && <Card title="No emails yet">Emails appear here as soon as they are sent.</Card>}

      {items.length > 0 && (
        <div className="grid gap-4 md:grid-cols-[minmax(0,20rem)_minmax(0,1fr)]">
          <div className={selectedId ? 'hidden md:block' : ''}>
            <ul aria-label="Emails" className="divide-y divide-surface-border rounded-card border border-surface-border bg-surface shadow-card">
              {items.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    onClick={() => setSelectedId(item.id)}
                    aria-current={item.id === selectedId ? 'true' : undefined}
                    className={`block w-full px-4 py-3 text-left text-sm hover:bg-surface-muted ${
                      item.id === selectedId ? 'bg-brand-50' : ''
                    }`}
                  >
                    <span className="block break-words font-medium text-ink">{item.subject}</span>
                    <span className="block break-all text-ink-muted">{item.to}</span>
                    <span className="mt-1 flex flex-wrap items-center gap-2 text-xs text-ink-muted">
                      <time dateTime={item.createdAt}>{new Date(item.createdAt).toLocaleString()}</time>
                      {item.deliveryError && (
                        <span className="rounded bg-red-50 px-1.5 py-0.5 font-medium text-red-700">Not delivered</span>
                      )}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
            {query.data && query.data.total > items.length && (
              <button type="button" className={`${secondaryButtonClass} mt-3 w-full`} onClick={() => setLimit(limit + PAGE_SIZE)}>
                Show more
              </button>
            )}
          </div>

          <div className={selectedId ? '' : 'hidden md:block'}>
            {selectedId ? (
              <EmailViewer id={selectedId} onBack={() => setSelectedId(null)} />
            ) : (
              <p className="text-sm text-ink-muted">Select an email to read it.</p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
