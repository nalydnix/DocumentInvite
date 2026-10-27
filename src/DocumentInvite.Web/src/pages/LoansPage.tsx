import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { ErrorList, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { StatusBadge } from '../components/StatusBadge';
import { ApiError, apiFetch } from '../lib/api';
import {
  formatAmount,
  LOANS_PAGE_SIZE,
  LOAN_WRITER_ROLES,
  STATUSES,
  STATUS_LABELS,
  type CollectionStatus,
  type LoanPage,
  type LoanSummary,
} from './loans';

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

function LoanCard({ loan }: { loan: LoanSummary }) {
  return (
    <li className="flex flex-wrap items-center justify-between gap-3 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="min-w-0">
        <Link to={`/loans/${loan.id}`} className="font-medium text-brand-700 hover:underline">
          {loan.reference} &middot; {loan.name}
        </Link>
        <p className="text-sm text-ink-muted">
          {loan.loanType} &middot; {formatAmount(loan.amount)} &middot; {loan.partyCount}{' '}
          {loan.partyCount === 1 ? 'party' : 'parties'}
        </p>
      </div>
      <StatusBadge status={loan.status} />
    </li>
  );
}

export function LoansPage() {
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, LOAN_WRITER_ROLES);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<CollectionStatus | ''>('');
  // The page belongs to the filter it was chosen for, so a new filter starts on page 1.
  const term = useDebounced(search.trim(), 250);
  const filterKey = `${term}|${status}`;
  const [pageFor, setPageFor] = useState({ key: '', page: 1 });
  const page = pageFor.key === filterKey ? pageFor.page : 1;
  const setPage = (update: (p: number) => number) => setPageFor({ key: filterKey, page: update(page) });

  const loans = useQuery({
    queryKey: ['loans', term, status, page],
    queryFn: () => {
      const params = new URLSearchParams({ page: String(page), pageSize: String(LOANS_PAGE_SIZE) });
      if (term) params.set('search', term);
      if (status) params.set('status', status);
      return apiFetch<LoanPage>(`/api/loans?${params}`);
    },
    placeholderData: keepPreviousData,
  });

  const data = loans.data;
  const pageCount = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const filtered = term !== '' || status !== '';

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold text-ink">Loans</h1>
        {canWrite && (
          <Link to="/loans/new" className={primaryButtonClass}>
            New loan
          </Link>
        )}
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <label className="block text-sm font-medium text-ink sm:col-span-2">
          Search loans
          <input
            type="search"
            className={inputClass}
            placeholder="Reference, name or loan type"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>
        <label className="block text-sm font-medium text-ink">
          Status
          <select
            className={inputClass}
            value={status}
            onChange={(e) => setStatus(e.target.value as CollectionStatus | '')}
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

      {loans.isPending && <p className="text-sm text-ink-muted">Loading loans...</p>}
      {loans.isError && (
        <ErrorList messages={loans.error instanceof ApiError ? loans.error.messages : ['Could not load loans.']} />
      )}
      {data && data.items.length === 0 && (
        <p className="text-sm text-ink-muted">{filtered ? 'No loans match your search.' : 'No loans yet.'}</p>
      )}
      {data && data.items.length > 0 && (
        <ul className="space-y-3" aria-label="Loans">
          {data.items.map((loan) => (
            <LoanCard key={loan.id} loan={loan} />
          ))}
        </ul>
      )}

      {data && pageCount > 1 && (
        <nav aria-label="Pagination" className="flex items-center justify-between gap-3">
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={page <= 1}
            onClick={() => setPage((p) => p - 1)}
          >
            Previous
          </button>
          <span className="text-sm text-ink-muted">
            Page {data.page} of {pageCount}
          </span>
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={page >= pageCount}
            onClick={() => setPage((p) => p + 1)}
          >
            Next
          </button>
        </nav>
      )}
    </div>
  );
}
