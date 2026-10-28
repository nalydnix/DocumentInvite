import { STATUS_LABELS, type CollectionStatus } from '../pages/loans';

const STYLES: Record<CollectionStatus, string> = {
  Sent: 'bg-surface-muted text-ink-muted',
  InProgress: 'bg-amber-50 text-amber-700',
  Submitted: 'bg-brand-50 text-brand-700',
  Completed: 'bg-green-50 text-green-700',
};

export function StatusBadge({ status }: { status: CollectionStatus }) {
  return (
    <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STYLES[status]}`}>{STATUS_LABELS[status]}</span>
  );
}
