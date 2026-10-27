import type { InputHTMLAttributes } from 'react';

export const inputClass =
  'mt-1 block w-full rounded-lg border border-surface-border bg-surface px-3 py-2 text-sm text-ink ' +
  'focus:border-brand-500 focus:outline-none focus:ring-2 focus:ring-brand-200';

export const primaryButtonClass =
  'inline-flex items-center justify-center rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white ' +
  'hover:bg-brand-700 focus:outline-none focus:ring-2 focus:ring-brand-200 disabled:opacity-60';

export const secondaryButtonClass =
  'inline-flex items-center justify-center rounded-lg border border-surface-border bg-surface px-4 py-2 ' +
  'text-sm font-medium text-ink hover:bg-surface-muted focus:outline-none focus:ring-2 focus:ring-brand-200 ' +
  'disabled:opacity-60';

export function Field({ label, ...props }: { label: string } & InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="block text-sm font-medium text-ink">
      {label}
      <input className={inputClass} {...props} />
    </label>
  );
}

export function ErrorList({ messages }: { messages: string[] }) {
  if (messages.length === 0) return null;
  return (
    <ul role="alert" className="space-y-1 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
      {messages.map((message) => (
        <li key={message}>{message}</li>
      ))}
    </ul>
  );
}
