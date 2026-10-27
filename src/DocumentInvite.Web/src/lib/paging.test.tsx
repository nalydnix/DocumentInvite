import type { ReactNode } from 'react';
import { act, renderHook } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { pageCountOf, rowRange, useClampPage, usePaging } from './paging';

function wrapper(route: string) {
  return ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>;
}

function useTestPaging(total?: number) {
  const paging = usePaging();
  useClampPage(paging, total);
  return { paging, search: useLocation().search };
}

describe('paging maths', () => {
  it('counts pages, always at least one', () => {
    expect(pageCountOf(0, 25)).toBe(1);
    expect(pageCountOf(25, 25)).toBe(1);
    expect(pageCountOf(26, 25)).toBe(2);
  });

  it('works out the visible row range', () => {
    expect(rowRange(1, 25, 0)).toEqual({ from: 0, to: 0 });
    expect(rowRange(2, 25, 60)).toEqual({ from: 26, to: 50 });
    expect(rowRange(3, 25, 60)).toEqual({ from: 51, to: 60 });
  });
});

describe('usePaging', () => {
  it('defaults to page 1 of 25', () => {
    const { result } = renderHook(() => usePaging(), { wrapper: wrapper('/') });

    expect(result.current.page).toBe(1);
    expect(result.current.pageSize).toBe(25);
    expect(result.current.params().toString()).toBe('page=1&pageSize=25');
  });

  it('reads the page and size from the URL and ignores junk', () => {
    const good = renderHook(() => usePaging(), { wrapper: wrapper('/?page=3&pageSize=50') });
    expect(good.result.current.page).toBe(3);
    expect(good.result.current.pageSize).toBe(50);

    const bad = renderHook(() => usePaging(), { wrapper: wrapper('/?page=abc&pageSize=-4') });
    expect(bad.result.current.page).toBe(1);
    expect(bad.result.current.pageSize).toBe(25);

    const huge = renderHook(() => usePaging(), { wrapper: wrapper('/?pageSize=5000') });
    expect(huge.result.current.pageSize).toBe(100);
  });

  it('writes the page to the URL, keeps other parameters and omits page 1', () => {
    const { result } = renderHook(() => useTestPaging(), { wrapper: wrapper('/?q=abc') });

    act(() => result.current.paging.setPage(4));
    expect(result.current.search).toBe('?q=abc&page=4');

    act(() => result.current.paging.setPage(1));
    expect(result.current.search).toBe('?q=abc');
  });

  it('goes back to page 1 when the page size changes or a filter resets it', () => {
    const { result } = renderHook(() => useTestPaging(), { wrapper: wrapper('/?page=3') });

    act(() => result.current.paging.setPageSize(50));
    expect(result.current.search).toBe('?pageSize=50');

    act(() => result.current.paging.setPage(2));
    act(() => result.current.paging.resetPage());
    expect(result.current.search).toBe('?pageSize=50');
    expect(result.current.paging.page).toBe(1);
  });

  it('falls back to the last page when the page is past the end', () => {
    const { result } = renderHook(() => useTestPaging(60), { wrapper: wrapper('/?page=9') });

    expect(result.current.paging.page).toBe(3);
    expect(result.current.search).toBe('?page=3');
  });

  it('leaves a valid page alone', () => {
    const { result } = renderHook(() => useTestPaging(60), { wrapper: wrapper('/?page=2') });

    expect(result.current.paging.page).toBe(2);
  });
});
