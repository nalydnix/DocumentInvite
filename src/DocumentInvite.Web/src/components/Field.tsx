import type { InputHTMLAttributes } from 'react';

export const inputClass =
  'mt-1 block w-full rounded-lg border border-surface-border bg-surface px-3 py-2 text-sm text-ink ' +
  'focus:border-brand-600 focus:outline-none focus:ring-2 focus:ring-brand-600/30';

export const primaryButtonClass =
  'inline-flex items-center justify-center rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white ' +
  'hover:bg-brand-700 disabled:cursor-not-allowed disabled:opacity-60';

export const secondaryButtonClass =
  'inline-flex items-center justify-center rounded-lg border border-surface-border bg-surface px-4 py-2 ' +
  'text-sm font-medium text-ink hover:border-brand-200 hover:bg-brand-50 ' +
  'disabled:cursor-not-allowed disabled:opacity-60';

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
    <ul role="alert" className="space-y-1 rounded-lg border border-danger-200 bg-danger-50 p-3 text-sm text-danger-700">
      {messages.map((message) => (
        <li key={message}>{message}</li>
      ))}
    </ul>
  );
}
