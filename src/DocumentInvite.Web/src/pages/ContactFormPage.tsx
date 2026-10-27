import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { ErrorList, Field, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { CONTACT_WRITER_ROLES, PARTY_TYPES, partyLabel, type Contact, type PartyType } from './contacts';

interface FormValues {
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
  partyType: PartyType;
}

const EMPTY: FormValues = { firstName: '', lastName: '', email: '', phone: '', partyType: 'Borrower' };

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function ContactForm({ contact, readOnly }: { contact?: Contact; readOnly: boolean }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [values, setValues] = useState<FormValues>(
    contact
      ? {
          firstName: contact.firstName,
          lastName: contact.lastName,
          email: contact.email,
          phone: contact.phone ?? '',
          partyType: contact.partyType,
        }
      : EMPTY,
  );
  const set = <K extends keyof FormValues>(key: K, value: FormValues[K]) =>
    setValues((current) => ({ ...current, [key]: value }));

  const save = useMutation({
    mutationFn: () =>
      apiFetch<Contact>(contact ? `/api/contacts/${contact.id}` : '/api/contacts', {
        method: contact ? 'PUT' : 'POST',
        body: { ...values, phone: values.phone.trim() || null },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['contacts'] });
      await queryClient.invalidateQueries({ queryKey: ['contact'] });
      navigate('/contacts');
    },
  });

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  return (
    <form onSubmit={handleSubmit} className="grid gap-3 sm:grid-cols-2">
      <Field
        label="First name"
        required
        maxLength={100}
        disabled={readOnly}
        value={values.firstName}
        onChange={(e) => set('firstName', e.target.value)}
      />
      <Field
        label="Last name"
        required
        maxLength={100}
        disabled={readOnly}
        value={values.lastName}
        onChange={(e) => set('lastName', e.target.value)}
      />
      <Field
        label="Email"
        type="email"
        required
        maxLength={256}
        disabled={readOnly}
        value={values.email}
        onChange={(e) => set('email', e.target.value)}
      />
      <Field
        label="Phone (optional)"
        type="tel"
        maxLength={30}
        disabled={readOnly}
        value={values.phone}
        onChange={(e) => set('phone', e.target.value)}
      />
      <label className="block text-sm font-medium text-ink sm:col-span-2">
        Party type
        <select
          className={inputClass}
          disabled={readOnly}
          value={values.partyType}
          onChange={(e) => set('partyType', e.target.value as PartyType)}
        >
          {PARTY_TYPES.map((type) => (
            <option key={type} value={type}>
              {partyLabel(type)}
            </option>
          ))}
        </select>
      </label>
      <div className="space-y-3 sm:col-span-2">
        <ErrorList messages={messagesOf(save.error)} />
        <div className="flex flex-wrap gap-3">
          {!readOnly && (
            <button type="submit" disabled={save.isPending} className={primaryButtonClass}>
              {contact ? 'Save changes' : 'Create contact'}
            </button>
          )}
          <Link to="/contacts" className={secondaryButtonClass}>
            {readOnly ? 'Back to contacts' : 'Cancel'}
          </Link>
        </div>
      </div>
    </form>
  );
}

/** Create (no id in the route) or edit a contact. Viewers see the details read-only. */
export function ContactFormPage() {
  const { id } = useParams();
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, CONTACT_WRITER_ROLES);
  const contact = useQuery({
    queryKey: ['contact', id],
    queryFn: () => apiFetch<Contact>(`/api/contacts/${id}`),
    enabled: id !== undefined,
  });

  const heading = id ? (canWrite ? 'Edit contact' : 'Contact') : 'New contact';

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">{heading}</h1>
      {!id && !canWrite && <Card title="Not authorised">Your role cannot create contacts.</Card>}
      {!id && canWrite && (
        <Card title="Contact details">
          <ContactForm readOnly={false} />
        </Card>
      )}
      {id && contact.isPending && <p className="text-sm text-ink-muted">Loading contact...</p>}
      {id && contact.isError && (
        <ErrorList
          messages={
            contact.error instanceof ApiError && contact.error.status === 404
              ? ['This contact does not exist.']
              : messagesOf(contact.error)
          }
        />
      )}
      {id && contact.data && (
        <Card title="Contact details">
          {contact.data.isPseudonymised && (
            <p className="mb-3">This contact has been pseudonymised and can no longer be edited.</p>
          )}
          <ContactForm contact={contact.data} readOnly={!canWrite || contact.data.isPseudonymised} />
        </Card>
      )}
    </div>
  );
}
