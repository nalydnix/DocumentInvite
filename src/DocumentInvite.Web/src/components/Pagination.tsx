import { pageCountOf, PAGE_SIZE_OPTIONS, rowRange } from '../lib/paging';
import { inputClass, secondaryButtonClass } from './Field';

interface PaginationProps {
  page: number;
  pageSize: number;
  total: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  pageSizeOptions?: readonly number[];
}

/** Previous/next, "Showing x-y of n", the page indicator and a page-size selector. Stacks on narrow screens. */
export function Pagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = PAGE_SIZE_OPTIONS,
}: PaginationProps) {
  if (total <= 0) return null;

  const pageCount = pageCountOf(total, pageSize);
  const current = Math.min(page, pageCount);
  const { from, to } = rowRange(current, pageSize, total);
  const options = pageSizeOptions.includes(pageSize)
    ? pageSizeOptions
    : [...pageSizeOptions, pageSize].sort((a, b) => a - b);

  return (
    <nav aria-label="Pagination" className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <p className="text-sm text-ink-muted">
        Showing {from}–{to} of {total}
      </p>
      <div className="flex flex-wrap items-center justify-between gap-3 sm:justify-end">
        <label className="flex items-center gap-2 text-sm text-ink-muted">
          Rows per page
          <select
            className={`${inputClass} mt-0 w-auto`}
            value={pageSize}
            onChange={(e) => onPageSizeChange(Number(e.target.value))}
          >
            {options.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </select>
        </label>
        <div className="flex items-center gap-3">
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={current <= 1}
            onClick={() => onPageChange(current - 1)}
          >
            Previous
          </button>
          <span className="text-sm text-ink-muted">
            Page {current} of {pageCount}
          </span>
          <button
            type="button"
            className={secondaryButtonClass}
            disabled={current >= pageCount}
            onClick={() => onPageChange(current + 1)}
          >
            Next
          </button>
        </div>
      </div>
    </nav>
  );
}
