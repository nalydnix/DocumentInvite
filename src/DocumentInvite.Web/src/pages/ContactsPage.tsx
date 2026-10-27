import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Pagination } from '../components/Pagination';
import { ErrorList, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { useClampPage, usePaging } from '../lib/paging';
import {
  CONTACT_WRITER_ROLES,
  partyLabel,
  type Contact,
  type ContactPage,
} from './contacts';

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

function ContactCard({ contact, canWrite }: { contact: Contact; canWrite: boolean }) {
  const name = `${contact.firstName} ${contact.lastName}`;
  return (
    <li className="flex flex-wrap items-center justify-between gap-3 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="min-w-0">
        <p className="font-medium text-ink">{name}</p>
        <p className="break-all text-sm text-ink-muted">{contact.email}</p>
        {contact.phone && <p className="text-sm text-ink-muted">{contact.phone}</p>}
      </div>
      <div className="flex items-center gap-3">
        <span className="rounded-full bg-brand-50 px-2 py-0.5 text-xs font-medium text-brand-700">
          {partyLabel(contact.partyType)}
        </span>
        <Link
          to={`/contacts/${contact.id}`}
          className={secondaryButtonClass}
          aria-label={`${canWrite ? 'Edit' : 'View'} ${name}`}
        >
          {canWrite ? 'Edit' : 'View'}
        </Link>
      </div>
    </li>
  );
}

export function ContactsPage() {
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, CONTACT_WRITER_ROLES);
  const [search, setSearch] = useState('');
  const paging = usePaging();
  const term = useDebounced(search.trim(), 250);

  const contacts = useQuery({
    queryKey: ['contacts', term, paging.page, paging.pageSize],
    queryFn: () => {
      const params = paging.params();
      if (term) params.set('search', term);
      return apiFetch<ContactPage>(`/api/contacts?${params}`);
    },
    placeholderData: keepPreviousData,
  });

  const data = contacts.data;
  useClampPage(paging, data?.total);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold text-ink">Contacts</h1>
        {canWrite && (
          <Link to="/contacts/new" className={primaryButtonClass}>
            New contact
          </Link>
        )}
      </div>

      <label className="block text-sm font-medium text-ink">
        Search contacts
        <input
          type="search"
          className={inputClass}
          placeholder="Name or email"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            paging.resetPage();
          }}
        />
      </label>

      {contacts.isPending && <p className="text-sm text-ink-muted">Loading contacts...</p>}
      {contacts.isError && (
        <ErrorList
          messages={contacts.error instanceof ApiError ? contacts.error.messages : ['Could not load contacts.']}
        />
      )}
      {data && data.items.length === 0 && (
        <p className="text-sm text-ink-muted">{term ? 'No contacts match your search.' : 'No contacts yet.'}</p>
      )}
      {data && data.items.length > 0 && (
        <ul className="space-y-3" aria-label="Contacts">
          {data.items.map((contact) => (
            <ContactCard key={contact.id} contact={contact} canWrite={canWrite} />
          ))}
        </ul>
      )}

      {data && (
        <Pagination
          page={paging.page}
          pageSize={paging.pageSize}
          total={data.total}
          onPageChange={paging.setPage}
          onPageSizeChange={paging.setPageSize}
        />
      )}
    </div>
  );
}
