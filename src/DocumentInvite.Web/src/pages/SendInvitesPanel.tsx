import { useRef, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '../components/Card';
import { ErrorList, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import type { PagedResponse } from '../lib/paging';
import type { ContactPage } from './contacts';
import type { FieldType, Invite, ItemKind } from './loans';

interface TemplateSummary {
  id: string;
  name: string;
  isArchived: boolean;
}

interface TemplateDetail {
  id: string;
  name: string;
  items: {
    id: string;
    name: string;
    kind: ItemKind;
    instructions: string;
    allowMultiple: boolean;
    fieldType: FieldType | null;
    isRequired: boolean;
  }[];
}

interface DraftItem {
  key: string;
  selected: boolean;
  name: string;
  kind: ItemKind;
  instructions: string;
  allowMultiple: boolean;
  fieldType: FieldType | null;
  isRequired: boolean;
}

interface DraftParty {
  key: string;
  contactId: string;
  dueDate: string;
  reminder: string;
  password: string;
  templateId: string;
  items: DraftItem[];
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

/** Sends the invites: choose parties, apply a template to each, tick and edit items, set the dates. */
export function SendInvitesPanel({ loanId, existingInvites }: { loanId: string; existingInvites: Invite[] }) {
  const queryClient = useQueryClient();
  const counter = useRef(0);
  const nextKey = () => `k${(counter.current += 1)}`;
  const [parties, setParties] = useState<DraftParty[]>([]);
  const [templateError, setTemplateError] = useState<string[]>([]);

  const contacts = useQuery({
    queryKey: ['contacts', 'picker'],
    queryFn: () => apiFetch<ContactPage>('/api/contacts?page=1&pageSize=100'),
  });
  const templates = useQuery({
    queryKey: ['templates', 'active'],
    // A bounded lookup: the first 100 active templates, by name.
    queryFn: () => apiFetch<PagedResponse<TemplateSummary>>('/api/templates?page=1&pageSize=100'),
  });

  const send = useMutation({
    mutationFn: () =>
      apiFetch<Invite[]>(`/api/loans/${loanId}/invites`, {
        method: 'POST',
        body: {
          parties: parties.map((party) => ({
            contactId: party.contactId,
            dueDate: party.dueDate,
            reminderIntervalDays: party.reminder.trim() === '' ? null : Number(party.reminder),
            password: party.password === '' ? null : party.password,
            items: party.items
              .filter((item) => item.selected)
              .map(({ name, kind, instructions, allowMultiple, fieldType, isRequired }) => ({
                name,
                kind,
                instructions,
                allowMultiple,
                fieldType,
                isRequired,
              })),
          })),
        },
      }),
    onSuccess: async () => {
      setParties([]);
      await queryClient.invalidateQueries({ queryKey: ['loan', loanId] });
      await queryClient.invalidateQueries({ queryKey: ['loans'] });
    },
  });

  const update = (key: string, patch: Partial<DraftParty>) =>
    setParties((current) => current.map((party) => (party.key === key ? { ...party, ...patch } : party)));
  const updateItem = (partyKey: string, itemKey: string, patch: Partial<DraftItem>) =>
    setParties((current) =>
      current.map((party) =>
        party.key === partyKey
          ? { ...party, items: party.items.map((item) => (item.key === itemKey ? { ...item, ...patch } : item)) }
          : party,
      ),
    );

  async function applyTemplate(partyKey: string, templateId: string) {
    update(partyKey, { templateId });
    setTemplateError([]);
    if (!templateId) {
      update(partyKey, { items: [] });
      return;
    }
    try {
      const template = await apiFetch<TemplateDetail>(`/api/templates/${templateId}`);
      update(partyKey, {
        items: template.items.map((item) => ({
          key: nextKey(),
          selected: true,
          name: item.name,
          kind: item.kind,
          instructions: item.instructions,
          allowMultiple: item.allowMultiple,
          fieldType: item.fieldType,
          isRequired: item.isRequired,
        })),
      });
    } catch (error) {
      setTemplateError(messagesOf(error));
    }
  }

  const alreadyInvited = new Set(existingInvites.map((invite) => invite.contactId));
  const chosenElsewhere = (partyKey: string) =>
    new Set(parties.filter((p) => p.key !== partyKey && p.contactId).map((p) => p.contactId));
  const availableContacts = (partyKey: string) =>
    (contacts.data?.items ?? []).filter(
      (c) => !c.isPseudonymised && !alreadyInvited.has(c.id) && !chosenElsewhere(partyKey).has(c.id),
    );

  const ready =
    parties.length > 0 &&
    parties.every(
      (party) => party.contactId && party.dueDate && party.items.some((item) => item.selected && item.name.trim()),
    );

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    send.mutate();
  }

  return (
    <Card title="Invite parties">
      <form onSubmit={handleSubmit} className="space-y-4">
        {parties.length === 0 && (
          <p>Add a party, choose a template, tick the items they should receive, then send their secure links.</p>
        )}

        {parties.map((party, index) => (
          <fieldset
            key={party.key}
            className="space-y-3 rounded-lg border border-surface-border p-3"
          >
            <div className="grid gap-3 sm:grid-cols-2">
              <label className="block text-sm font-medium text-ink">
                Party {index + 1}
                <select
                  className={inputClass}
                  value={party.contactId}
                  onChange={(e) => update(party.key, { contactId: e.target.value })}
                >
                  <option value="">Choose a contact...</option>
                  {availableContacts(party.key).map((contact) => (
                    <option key={contact.id} value={contact.id}>
                      {contact.firstName} {contact.lastName} ({contact.email})
                    </option>
                  ))}
                </select>
              </label>
              <label className="block text-sm font-medium text-ink">
                Template
                <select
                  className={inputClass}
                  value={party.templateId}
                  onChange={(e) => void applyTemplate(party.key, e.target.value)}
                >
                  <option value="">Choose a template...</option>
                  {(templates.data?.items ?? [])
                    .filter((t) => !t.isArchived)
                    .map((template) => (
                      <option key={template.id} value={template.id}>
                        {template.name}
                      </option>
                    ))}
                </select>
              </label>
              <label className="block text-sm font-medium text-ink">
                Due date
                <input
                  type="date"
                  className={inputClass}
                  value={party.dueDate}
                  onChange={(e) => update(party.key, { dueDate: e.target.value })}
                />
              </label>
              <label className="block text-sm font-medium text-ink">
                Reminder every (days)
                <input
                  type="number"
                  min={1}
                  max={365}
                  className={inputClass}
                  placeholder="Default"
                  value={party.reminder}
                  onChange={(e) => update(party.key, { reminder: e.target.value })}
                />
              </label>
              <label className="block text-sm font-medium text-ink sm:col-span-2">
                Portal password (optional)
                <input
                  type="password"
                  autoComplete="new-password"
                  minLength={8}
                  maxLength={128}
                  className={inputClass}
                  aria-describedby={`password-help-${party.key}`}
                  value={party.password}
                  onChange={(e) => update(party.key, { password: e.target.value })}
                />
                <span id={`password-help-${party.key}`} className="mt-1 block text-xs font-normal text-ink-muted">
                  If set, the party must enter it after opening the link. Share it separately from the email. 8 to 128
                  characters.
                </span>
              </label>
            </div>

            {party.items.length > 0 && (
              <ul className="space-y-3" aria-label={`Items for party ${index + 1}`}>
                {party.items.map((item) => (
                  <li key={item.key} className="space-y-2 rounded-lg bg-surface-muted p-3">
                    <div className="flex flex-wrap items-center gap-3">
                      <input
                        type="checkbox"
                        aria-label={`Include ${item.name || 'item'}`}
                        checked={item.selected}
                        onChange={(e) => updateItem(party.key, item.key, { selected: e.target.checked })}
                      />
                      <input
                        className={`${inputClass} !mt-0 min-w-0 flex-1`}
                        aria-label={`Name of ${item.name || 'item'}`}
                        maxLength={200}
                        value={item.name}
                        onChange={(e) => updateItem(party.key, item.key, { name: e.target.value })}
                      />
                      <label className="flex items-center gap-1 text-sm text-ink">
                        <input
                          type="checkbox"
                          checked={item.isRequired}
                          onChange={(e) => updateItem(party.key, item.key, { isRequired: e.target.checked })}
                        />
                        Required
                      </label>
                    </div>
                    <textarea
                      className={inputClass}
                      aria-label={`Instructions for ${item.name || 'item'}`}
                      maxLength={2000}
                      rows={2}
                      value={item.instructions}
                      onChange={(e) => updateItem(party.key, item.key, { instructions: e.target.value })}
                    />
                  </li>
                ))}
              </ul>
            )}

            <button
              type="button"
              className={secondaryButtonClass}
              onClick={() => setParties((current) => current.filter((p) => p.key !== party.key))}
            >
              Remove party {index + 1}
            </button>
          </fieldset>
        ))}

        <ErrorList messages={templateError} />
        <ErrorList messages={messagesOf(send.error)} />
        {send.isSuccess && <p role="status">Invites sent. Each party has been emailed a secure link.</p>}

        <div className="flex flex-wrap gap-3">
          <button
            type="button"
            className={secondaryButtonClass}
            onClick={() =>
              setParties((current) => [
                ...current,
                { key: nextKey(), contactId: '', dueDate: '', reminder: '', password: '', templateId: '', items: [] },
              ])
            }
          >
            Add party
          </button>
          {parties.length > 0 && (
            <button type="submit" disabled={!ready || send.isPending} className={primaryButtonClass}>
              Send invites
            </button>
          )}
        </div>
      </form>
    </Card>
  );
}
