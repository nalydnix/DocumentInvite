import type { PartyType } from './contacts';

export type CollectionStatus = 'Sent' | 'InProgress' | 'Submitted' | 'Completed';
export type ItemKind = 'FileUpload' | 'FormField';
export type FieldType = 'Text' | 'Number' | 'Date' | 'YesNo';
export type RequestItemStatus = 'Outstanding' | 'Submitted' | 'Approved' | 'Rejected';

export const STATUS_LABELS: Record<CollectionStatus, string> = {
  Sent: 'Sent',
  InProgress: 'In progress',
  Submitted: 'Submitted',
  Completed: 'Completed',
};

export const STATUSES = Object.keys(STATUS_LABELS) as CollectionStatus[];

export interface LoanSummary {
  id: string;
  reference: string;
  name: string;
  amount: number;
  loanType: string;
  status: CollectionStatus;
  createdAt: string;
  completedAt: string | null;
  partyCount: number;
  itemsTotal: number;
  itemsApproved: number;
  percentComplete: number;
  /** The earliest due date of the invites still open, or null when none is. */
  dueDate: string | null;
  isOverdue: boolean;
}


export interface InviteItem {
  id: string;
  name: string;
  kind: ItemKind;
  instructions: string;
  allowMultiple: boolean;
  fieldType: FieldType | null;
  isRequired: boolean;
  sortOrder: number;
  status: RequestItemStatus;
  rejectionReason: string | null;
  fieldValue: string | null;
  partyNote: string | null;
  reviewedAt: string | null;
  files: ItemFile[];
}

export interface ItemFile {
  id: string;
  fileName: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  sequence: number;
  uploadedAt: string | null;
}

export interface ReadUrl {
  url: string;
  expiresAt: string;
  fileName: string;
  contentType: string;
}

export interface Invite {
  id: string;
  loanId: string;
  contactId: string;
  partyName: string;
  partyEmail: string;
  partyType: PartyType;
  dueDate: string;
  reminderIntervalDays: number;
  sentAt: string;
  tokenExpiresAt: string;
  revokedAt: string | null;
  status: CollectionStatus;
  hasPassword: boolean;
  items: InviteItem[];
}

export interface Loan
  extends Omit<LoanSummary, 'partyCount' | 'itemsTotal' | 'itemsApproved' | 'percentComplete' | 'dueDate' | 'isOverdue'> {
  invites: Invite[];
}

/** Roles that may create loans and send, revoke and reissue invites (the viewer role is read-only). */
export const LOAN_WRITER_ROLES = ['Admin', 'LoanOfficer'] as const;

export function formatAmount(amount: number): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

export function formatDate(value: string): string {
  return new Date(value).toLocaleDateString('en-US', { dateStyle: 'medium', timeZone: 'UTC' });
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
