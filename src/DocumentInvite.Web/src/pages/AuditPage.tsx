import { useState, type FormEvent } from 'react';
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { Card } from '../components/Card';
import { ErrorList, Field, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiDownload, apiFetch } from '../lib/api';

export const AUDIT_PAGE_SIZE = 25;

export interface AuditEvent {
  id: number;
  occurredAt: string;
  actorType: string;
  actorId: string | null;
  action: string;
  targetType: string;
  targetId: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  correlationId: string;
  detailsJson: string;
}

interface AuditPageData {
  items: AuditEvent[];
  total: number;
  page: number;
  pageSize: number;
}

interface VerifyResult {
  isValid: boolean;
  firstBrokenEventId: number | null;
  eventsChecked: number;
}

interface Filter {
  from: string;
  to: string;
  actorId: string;
  action: string;
  targetType: string;
  targetId: string;
}

const emptyFilter: Filter = { from: '', to: '', actorId: '', action: '', targetType: '', targetId: '' };

/** The range runs from the start of the first day to the end of the last day, in the viewer's time zone. */
function filterParams(filter: Filter): URLSearchParams {
  const params = new URLSearchParams();
  if (filter.from) params.set('from', new Date(`${filter.from}T00:00:00`).toISOString());
  if (filter.to) {
    const end = new Date(`${filter.to}T00:00:00`);
    end.setDate(end.getDate() + 1);
    params.set('to', end.toISOString());
  }
  if (filter.actorId.trim()) params.set('actorId', filter.actorId.trim());
  if (filter.action.trim()) params.set('action', filter.action.trim());
  if (filter.targetType.trim()) params.set('targetType', filter.targetType.trim());
  if (filter.targetId.trim()) params.set('targetId', filter.targetId.trim());
  return params;
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function FilterForm({ applied, onApply }: { applied: Filter; onApply: (filter: Filter) => void }) {
  const [draft, setDraft] = useState<Filter>(applied);
  const set = (key: keyof Filter) => (e: { target: { value: string } }) =>
    setDraft((d) => ({ ...d, [key]: e.target.value }));

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    onApply(draft);
  }

  return (
    <form
      onSubmit={handleSubmit}
      aria-label="Audit filters"
      className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3"
    >
      <Field label="From date" type="date" value={draft.from} onChange={set('from')} />
      <Field label="To date" type="date" value={draft.to} onChange={set('to')} />
      <Field label="Action" placeholder="for example user.created" value={draft.action} onChange={set('action')} />
      <Field label="Actor id" placeholder="User id" value={draft.actorId} onChange={set('actorId')} />
      <Field label="Target type" placeholder="for example contact" value={draft.targetType} onChange={set('targetType')} />
      <Field label="Target id" placeholder="Id of the record" value={draft.targetId} onChange={set('targetId')} />
      <div className="flex flex-wrap gap-3 sm:col-span-2 lg:col-span-3">
        <button type="submit" className={primaryButtonClass}>
          Apply filters
        </button>
        <button
          type="button"
          className={secondaryButtonClass}
          onClick={() => {
            setDraft(emptyFilter);
            onApply(emptyFilter);
          }}
        >
          Clear filters
        </button>
      </div>
    </form>
  );
}

function EventCard({ event }: { event: AuditEvent }) {
  return (
    <li className="space-y-1 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p className="font-mono text-sm font-medium text-ink">{event.action}</p>
        <time dateTime={event.occurredAt} className="text-xs text-ink-muted">
          {new Date(event.occurredAt).toLocaleString()}
        </time>
      </div>
      <dl className="grid gap-x-4 gap-y-1 text-xs text-ink-muted sm:grid-cols-2">
        <div className="break-all">
          <dt className="inline font-medium">Actor: </dt>
          <dd className="inline">
            {event.actorType}
            {event.actorId ? ` ${event.actorId}` : ''}
          </dd>
        </div>
        <div className="break-all">
          <dt className="inline font-medium">Target: </dt>
          <dd className="inline">
            {event.targetType}
            {event.targetId ? ` ${event.targetId}` : ''}
          </dd>
        </div>
        {event.ipAddress && (
          <div>
            <dt className="inline font-medium">IP: </dt>
            <dd className="inline">{event.ipAddress}</dd>
          </div>
        )}
        <div className="break-all">
          <dt className="inline font-medium">Details: </dt>
          <dd className="inline font-mono">{event.detailsJson}</dd>
        </div>
      </dl>
    </li>
  );
}

function VerifyPanel() {
  const verify = useMutation({ mutationFn: () => apiFetch<VerifyResult>('/api/audit/verify', { method: 'POST' }) });
  const result = verify.data;

  return (
    <Card title="Integrity">
      <div className="space-y-3">
        <p>Walk the whole hash chain and report the first event that does not match.</p>
        <button
          type="button"
          className={secondaryButtonClass}
          disabled={verify.isPending}
          onClick={() => verify.mutate()}
        >
          {verify.isPending ? 'Verifying...' : 'Verify integrity'}
        </button>
        <ErrorList messages={messagesOf(verify.error)} />
        {result && (
          <p
            role="status"
            className={`rounded-lg border p-3 text-sm ${
              result.isValid ? 'border-green-200 bg-green-50 text-green-800' : 'border-red-200 bg-red-50 text-red-700'
            }`}
          >
            {result.isValid
              ? `Audit trail is valid (${result.eventsChecked} events checked).`
              : `Audit trail is NOT valid. The first broken event is #${result.firstBrokenEventId}.`}
          </p>
        )}
      </div>
    </Card>
  );
}

export function AuditPage() {
  const [filter, setFilter] = useState<Filter>(emptyFilter);
  // The page belongs to the filter it was chosen for, so applying a new filter starts on page 1.
  const [pageFor, setPageFor] = useState({ filter, page: 1 });
  const page = pageFor.filter === filter ? pageFor.page : 1;
  const setPage = (next: number) => setPageFor({ filter, page: next });

  const events = useQuery({
    queryKey: ['audit', filter, page],
    queryFn: () => {
      const params = filterParams(filter);
      params.set('page', String(page));
      params.set('pageSize', String(AUDIT_PAGE_SIZE));
      return apiFetch<AuditPageData>(`/api/audit?${params}`);
    },
    placeholderData: keepPreviousData,
  });

  const exportCsv = useMutation({
    mutationFn: async () => {
      const blob = await apiDownload(`/api/audit/export.csv?${filterParams(filter)}`);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'audit-export.csv';
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    },
  });

  const data = events.data;
  const pageCount = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold text-ink">Audit log</h1>
        <button
          type="button"
          className={primaryButtonClass}
          disabled={exportCsv.isPending}
          onClick={() => exportCsv.mutate()}
        >
          Export CSV
        </button>
      </div>
      <ErrorList messages={messagesOf(exportCsv.error)} />

      <FilterForm applied={filter} onApply={setFilter} />
      <VerifyPanel />

      {events.isPending && <p className="text-sm text-ink-muted">Loading audit events...</p>}
      {events.isError && <ErrorList messages={messagesOf(events.error)} />}
      {data && data.items.length === 0 && <p className="text-sm text-ink-muted">No events match the filters.</p>}
      {data && data.items.length > 0 && (
        <>
          <p className="text-sm text-ink-muted">{data.total} events</p>
          <ul className="space-y-3" aria-label="Audit events">
            {data.items.map((event) => (
              <EventCard key={event.id} event={event} />
            ))}
          </ul>
        </>
      )}

      {data && pageCount > 1 && (
        <nav aria-label="Pagination" className="flex items-center justify-between gap-3">
          <button type="button" className={secondaryButtonClass} disabled={page <= 1} onClick={() => setPage(page - 1)}>
            Previous
          </button>
          <span className="text-sm text-ink-muted">
            Page {data.page} of {pageCount}
          </span>
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={page >= pageCount}
            onClick={() => setPage(page + 1)}
          >
            Next
          </button>
        </nav>
      )}
    </div>
  );
}
