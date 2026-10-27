import type { PagedResponse } from '../lib/paging';

export const PARTY_TYPES = ['Borrower', 'CoBorrower', 'Guarantor', 'Broker', 'Attorney', 'Accountant'] as const;
export type PartyType = (typeof PARTY_TYPES)[number];

const PARTY_LABELS: Record<PartyType, string> = {
  Borrower: 'Borrower',
  CoBorrower: 'Co-borrower',
  Guarantor: 'Guarantor',
  Broker: 'Broker',
  Attorney: 'Attorney',
  Accountant: 'Accountant',
};

export function partyLabel(type: PartyType): string {
  return PARTY_LABELS[type];
}

export interface Contact {
  id: string;
  firstName: string;
  lastName: string;
  email: string;
  phone: string | null;
  partyType: PartyType;
  isPseudonymised: boolean;
}

export type ContactPage = PagedResponse<Contact>;

/** Roles that may create and edit contacts (the viewer role is read-only). */
export const CONTACT_WRITER_ROLES = ['Admin', 'LoanOfficer'] as const;
