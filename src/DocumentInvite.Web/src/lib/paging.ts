import { useCallback, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';

/** The response shape of every collection endpoint (see "Pagination" in docs/architecture.md). */
export interface PagedResponse<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export const DEFAULT_PAGE_SIZE = 25;
export const MAX_PAGE_SIZE = 100;
export const PAGE_SIZE_OPTIONS = [10, 25, 50, 100] as const;

/** Parses a positive whole number from the query string, or returns the fallback. */
function positiveInt(value: string | null, fallback: number): number {
  if (value === null || !/^\d+$/.test(value)) return fallback;
  const n = Number(value);
  return n >= 1 && Number.isSafeInteger(n) ? n : fallback;
}

export function pageCountOf(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / pageSize));
}

/** The 1-based first and last row shown on a page; both are 0 when there are no rows. */
export function rowRange(page: number, pageSize: number, total: number): { from: number; to: number } {
  if (total <= 0) return { from: 0, to: 0 };
  const from = Math.min((page - 1) * pageSize + 1, total);
  return { from, to: Math.min(page * pageSize, total) };
}

export interface PagingState {
  page: number;
  pageSize: number;
  /** Goes to a page. Page 1 and the default size are left out of the URL. */
  setPage: (page: number, options?: { replace?: boolean }) => void;
  setPageSize: (pageSize: number) => void;
  /** Call when a filter or search changes, so the new results start on page 1. */
  resetPage: () => void;
  /** The query-string part for the API: `page=..&pageSize=..`. */
  params: () => URLSearchParams;
}

/**
 * Page state kept in the URL query string (`?page=3&pageSize=50`), so refresh, back and shared
 * links all land on the same page. Other query parameters are left untouched.
 */
export function usePaging(): PagingState {
  const [searchParams, setSearchParams] = useSearchParams();
  const page = positiveInt(searchParams.get('page'), 1);
  const requestedSize = positiveInt(searchParams.get('pageSize'), DEFAULT_PAGE_SIZE);
  const pageSize = Math.min(requestedSize, MAX_PAGE_SIZE);

  const update = useCallback(
    (nextPage: number, nextSize: number, replace: boolean) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current);
          if (nextPage > 1) next.set('page', String(nextPage));
          else next.delete('page');
          if (nextSize !== DEFAULT_PAGE_SIZE) next.set('pageSize', String(nextSize));
          else next.delete('pageSize');
          return next;
        },
        { replace },
      );
    },
    [setSearchParams],
  );

  const setPage = useCallback(
    (next: number, options?: { replace?: boolean }) => update(Math.max(1, next), pageSize, options?.replace ?? false),
    [update, pageSize],
  );
  const setPageSize = useCallback((next: number) => update(1, next, false), [update]);
  const resetPage = useCallback(() => {
    if (page !== 1) update(1, pageSize, true);
  }, [update, page, pageSize]);
  const params = useCallback(
    () => new URLSearchParams({ page: String(page), pageSize: String(pageSize) }),
    [page, pageSize],
  );

  return { page, pageSize, setPage, setPageSize, resetPage, params };
}

/** A page past the end falls back to the last page once the total is known. */
export function useClampPage(paging: PagingState, total: number | undefined) {
  const { page, pageSize, setPage } = paging;
  useEffect(() => {
    if (total === undefined) return;
    const last = pageCountOf(total, pageSize);
    if (page > last) setPage(last, { replace: true });
  }, [total, page, pageSize, setPage]);
}
