import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { ErrorList, inputClass, primaryButtonClass } from '../components/Field';
import { Pagination } from '../components/Pagination';
import { ProgressBar } from '../components/ProgressBar';
import { StatusBadge } from '../components/StatusBadge';
import { ApiError, apiFetch } from '../lib/api';
import { useClampPage, usePaging, type PagedResponse } from '../lib/paging';
import {
  formatAmount,
  formatDate,
  REQUEST_WRITER_ROLES,
  STATUSES,
  STATUS_LABELS,
  type CollectionStatus,
  type RequestSummary,
} from './requests';

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

function RequestCard({ request }: { request: RequestSummary }) {
  const label = `Progress for ${request.reference}`;
  return (
    <li className="grid gap-3 rounded-card border border-surface-border bg-surface p-4 shadow-card md:grid-cols-[minmax(0,2fr)_minmax(0,1.5fr)_auto] md:items-center">
      <div className="min-w-0">
        <Link to={`/requests/${request.id}`} className="font-medium text-brand-700 hover:underline">
          {request.reference} &middot; {request.name}
        </Link>
        <p className="text-sm text-ink-muted">
          {request.requestType} &middot; {formatAmount(request.amount)} &middot; {request.partyCount}{' '}
          {request.partyCount === 1 ? 'party' : 'parties'}
        </p>
      </div>
      <div className="space-y-1">
        <ProgressBar percent={request.percentComplete} label={label} />
        <p className="text-sm text-ink-muted">
          {request.percentComplete}% complete ({request.itemsApproved} of {request.itemsTotal} items approved)
          {request.dueDate && <> &middot; due {formatDate(request.dueDate)}</>}
        </p>
      </div>
      <div className="flex items-center gap-2 md:justify-end">
        {request.isOverdue && (
          <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs font-medium text-red-700">Overdue</span>
        )}
        <StatusBadge status={request.status} />
      </div>
    </li>
  );
}

export function RequestsPage() {
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, REQUEST_WRITER_ROLES);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<CollectionStatus | ''>('');
  const paging = usePaging();
  const term = useDebounced(search.trim(), 250);

  const requests = useQuery({
    queryKey: ['requests', term, status, paging.page, paging.pageSize],
    queryFn: () => {
      const params = paging.params();
      if (term) params.set('search', term);
      if (status) params.set('status', status);
      return apiFetch<PagedResponse<RequestSummary>>(`/api/requests?${params}`);
    },
    placeholderData: keepPreviousData,
  });

  const data = requests.data;
  useClampPage(paging, data?.total);
  const filtered = term !== '' || status !== '';

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold text-ink">Requests</h1>
        {canWrite && (
          <Link to="/requests/new" className={primaryButtonClass}>
            New request
          </Link>
        )}
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <label className="block text-sm font-medium text-ink sm:col-span-2">
          Search requests
          <input
            type="search"
            className={inputClass}
            placeholder="Reference, name or request type"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              paging.resetPage();
            }}
          />
        </label>
        <label className="block text-sm font-medium text-ink">
          Status
          <select
            className={inputClass}
            value={status}
            onChange={(e) => {
              setStatus(e.target.value as CollectionStatus | '');
              paging.resetPage();
            }}
          >
            <option value="">All statuses</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {STATUS_LABELS[s]}
              </option>
            ))}
          </select>
        </label>
      </div>

      {requests.isPending && <p className="text-sm text-ink-muted">Loading requests...</p>}
      {requests.isError && (
        <ErrorList messages={requests.error instanceof ApiError ? requests.error.messages : ['Could not load requests.']} />
      )}
      {data && data.items.length === 0 && (
        <p className="text-sm text-ink-muted">{filtered ? 'No requests match your search.' : 'No requests yet.'}</p>
      )}
      {data && data.items.length > 0 && (
        <ul className="space-y-3" aria-label="Requests">
          {data.items.map((request) => (
            <RequestCard key={request.id} request={request} />
          ))}
        </ul>
      )}

      {data && (
        <Pagination
          page={paging.page}
          pageSize={paging.pageSize}
          total={data.total}
          onPageChange={paging.setPage}
          onPageSizeChange={paging.setPageSize}
        />
      )}
    </div>
  );
}
