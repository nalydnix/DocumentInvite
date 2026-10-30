import { STATUS_LABELS, type CollectionStatus } from '../pages/requests';

const STYLES: Record<CollectionStatus, string> = {
  Sent: 'bg-surface-muted text-ink-muted ring-1 ring-inset ring-surface-border',
  InProgress: 'bg-warning-50 text-warning-700 ring-1 ring-inset ring-warning-200',
  Submitted: 'bg-brand-50 text-brand-700 ring-1 ring-inset ring-brand-200',
  Completed: 'bg-success-50 text-success-700 ring-1 ring-inset ring-success-200',
  ReadyToComplete: 'bg-success-50 text-success-700 ring-1 ring-inset ring-success-200',
};

export function StatusBadge({ status }: { status: CollectionStatus }) {
  return (
    <span className={`whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium ${STYLES[status]}`}>{STATUS_LABELS[status]}</span>
  );
}
