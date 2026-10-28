import { useEffect, useState, type FormEvent } from 'react';
import { inputClass, primaryButtonClass } from './Field';
import { Pagination } from './Pagination';
import { pageCountOf, type PagedResponse } from '../lib/paging';

export const MAX_MESSAGE_LENGTH = 4000;

export type MessageAuthor = 'Team' | 'Party' | 'System';

/** One entry of a thread, in the shape both the staff and the portal views can fill. */
export interface ThreadEntry {
  id: string;
  authorType: MessageAuthor;
  /** Shown above the text; the portal passes "Your lender" for the team. */
  authorLabel: string;
  body: string;
  /** True for an internal note. Only the staff view ever has these. */
  isInternal?: boolean;
  createdAt: string;
}

const dateTime = new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' });

function Entry({ entry, viewer }: { entry: ThreadEntry; viewer: 'staff' | 'party' }) {
  if (entry.authorType === 'System') {
    return (
      <li className="rounded-lg bg-surface-muted px-3 py-2 text-center text-xs text-ink-muted">
        <p className="whitespace-pre-wrap break-words">{entry.body}</p>
        <time dateTime={entry.createdAt}>{dateTime.format(new Date(entry.createdAt))}</time>
      </li>
    );
  }

  const mine = (viewer === 'staff') === (entry.authorType === 'Team');
  const tone = entry.isInternal
    ? 'border border-amber-200 bg-amber-50 text-ink'
    : mine
      ? 'bg-brand-600 text-white'
      : 'border border-surface-border bg-surface text-ink';
  const meta = entry.isInternal ? 'text-amber-800' : mine ? 'text-brand-50' : 'text-ink-muted';

  return (
    <li className={`flex ${mine ? 'justify-end' : 'justify-start'}`}>
      <div className={`max-w-[90%] rounded-lg px-3 py-2 text-sm sm:max-w-[80%] ${tone}`}>
        <p className={`text-xs font-medium ${meta}`}>
          {entry.isInternal && <span className="mr-1 rounded bg-amber-100 px-1 py-0.5">Internal note</span>}
          {entry.authorLabel}
        </p>
        <p className="mt-1 whitespace-pre-wrap break-words">{entry.body}</p>
        <time className={`mt-1 block text-xs ${meta}`} dateTime={entry.createdAt}>
          {dateTime.format(new Date(entry.createdAt))}
        </time>
      </div>
    </li>
  );
}

/**
 * A page of a thread, newest first, with the shared pagination control. The page lives in component
 * state because several threads can be on one screen, so it cannot share the URL query string.
 */
export function MessageList({
  label,
  viewer,
  data,
  entries,
  loading,
  error,
  page,
  pageSize,
  onPageChange,
  onPageSizeChange,
  emptyText,
}: {
  label: string;
  viewer: 'staff' | 'party';
  data: PagedResponse<unknown> | undefined;
  entries: ThreadEntry[];
  loading: boolean;
  error: string | null;
  page: number;
  pageSize: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  emptyText: string;
}) {
  const total = data?.total;
  useEffect(() => {
    if (total === undefined) return;
    const last = pageCountOf(total, pageSize);
    if (page > last) onPageChange(last);
  }, [total, page, pageSize, onPageChange]);

  if (loading) return <p className="text-sm text-ink-muted">Loading messages...</p>;
  if (error) return <p role="alert" className="text-sm text-red-700">{error}</p>;
  return (
    <div className="space-y-3">
      {entries.length === 0 ? (
        <p className="text-sm text-ink-muted">{emptyText}</p>
      ) : (
        <ul className="space-y-2" aria-label={label}>
          {entries.map((entry) => (
            <Entry key={entry.id} entry={entry} viewer={viewer} />
          ))}
        </ul>
      )}
      <Pagination
        page={page}
        pageSize={pageSize}
        total={total ?? 0}
        onPageChange={onPageChange}
        onPageSizeChange={onPageSizeChange}
        pageSizeOptions={[10, 25, 50]}
      />
    </div>
  );
}

/** A text box with a character counter. `onSubmit` resolves when the entry was saved, which clears the box. */
export function MessageForm({
  label,
  submitLabel,
  placeholder,
  disabled,
  onSubmit,
  extra,
}: {
  label: string;
  submitLabel: string;
  placeholder?: string;
  disabled?: boolean;
  onSubmit: (body: string) => Promise<void>;
  extra?: React.ReactNode;
}) {
  const [body, setBody] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (body.trim() === '') return;
    setBusy(true);
    try {
      await onSubmit(body.trim());
      setBody('');
    } catch {
      // The caller shows the error; the text stays so nothing typed is lost.
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-2">
      <label className="block text-sm font-medium text-ink">
        {label}
        <textarea
          className={`${inputClass} min-h-20 resize-y`}
          rows={3}
          maxLength={MAX_MESSAGE_LENGTH}
          placeholder={placeholder}
          value={body}
          onChange={(event) => setBody(event.target.value)}
        />
      </label>
      {extra}
      <div className="flex items-center justify-between gap-3">
        <span className="text-xs text-ink-muted">
          {body.length} / {MAX_MESSAGE_LENGTH}
        </span>
        <button
          type="submit"
          className={`${primaryButtonClass} min-h-11 sm:min-h-0`}
          disabled={disabled || busy || body.trim() === ''}
        >
          {busy ? 'Sending...' : submitLabel}
        </button>
      </div>
    </form>
  );
}
