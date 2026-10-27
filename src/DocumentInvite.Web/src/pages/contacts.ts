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

export interface ContactPage {
  items: Contact[];
  total: number;
  page: number;
  pageSize: number;
}

export const CONTACTS_PAGE_SIZE = 20;

/** Roles that may create and edit contacts (the viewer role is read-only). */
export const CONTACT_WRITER_ROLES = ['Admin', 'LoanOfficer'] as const;
