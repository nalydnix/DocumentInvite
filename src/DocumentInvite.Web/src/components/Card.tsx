import type { ReactNode } from 'react';

export function Card({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <section className="rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <h2 className="text-base font-semibold text-ink">{title}</h2>
      {children && <div className="mt-2 text-sm text-ink-muted">{children}</div>}
    </section>
  );
}
