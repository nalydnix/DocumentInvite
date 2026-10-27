import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { ErrorList, Field, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';

export type ItemKind = 'FileUpload' | 'FormField';
export type FieldType = 'Text' | 'Number' | 'Date' | 'YesNo';

const FIELD_TYPE_LABELS: Record<FieldType, string> = {
  Text: 'Text',
  Number: 'Number',
  Date: 'Date',
  YesNo: 'Yes / no',
};

export interface TemplateItem {
  id: string;
  name: string;
  kind: ItemKind;
  instructions: string;
  allowMultiple: boolean;
  fieldType: FieldType | null;
  isRequired: boolean;
  sortOrder: number;
}

export interface TemplateDetail {
  id: string;
  name: string;
  description: string;
  isSeeded: boolean;
  isArchived: boolean;
  items: TemplateItem[];
}

interface DraftItem {
  key: string;
  id: string | null;
  name: string;
  kind: ItemKind;
  instructions: string;
  allowMultiple: boolean;
  fieldType: FieldType | null;
  isRequired: boolean;
}

let nextKey = 0;
function newKey(): string {
  nextKey += 1;
  return `draft-${nextKey}`;
}

function blankItem(kind: ItemKind): DraftItem {
  return {
    key: newKey(),
    id: null,
    name: '',
    kind,
    instructions: '',
    allowMultiple: false,
    fieldType: kind === 'FormField' ? 'Text' : null,
    isRequired: true,
  };
}

function toDraft(item: TemplateItem): DraftItem {
  return { ...item, key: newKey() };
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function ItemEditor({
  item,
  index,
  count,
  disabled,
  onChange,
  onMove,
  onRemove,
}: {
  item: DraftItem;
  index: number;
  count: number;
  disabled: boolean;
  onChange: (item: DraftItem) => void;
  onMove: (direction: -1 | 1) => void;
  onRemove: () => void;
}) {
  const position = index + 1;
  return (
    <li className="space-y-3 rounded-lg border border-surface-border p-3" aria-label={`Item ${position}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-xs font-medium uppercase text-ink-muted">
          {position}. {item.kind === 'FileUpload' ? 'File upload' : 'Form field'}
        </span>
        {!disabled && (
          <div className="flex gap-2">
            <button
              type="button"
              className={secondaryButtonClass}
              disabled={index === 0}
              onClick={() => onMove(-1)}
              aria-label={`Move item ${position} up`}
            >
              Up
            </button>
            <button
              type="button"
              className={secondaryButtonClass}
              disabled={index === count - 1}
              onClick={() => onMove(1)}
              aria-label={`Move item ${position} down`}
            >
              Down
            </button>
            <button type="button" className={secondaryButtonClass} onClick={onRemove} aria-label={`Remove item ${position}`}>
              Remove
            </button>
          </div>
        )}
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field
          label={`Name of item ${position}`}
          required
          disabled={disabled}
          maxLength={200}
          value={item.name}
          onChange={(e) => onChange({ ...item, name: e.target.value })}
        />
        {item.kind === 'FormField' && (
          <label className="block text-sm font-medium text-ink">
            {`Field type of item ${position}`}
            <select
              className={inputClass}
              disabled={disabled}
              value={item.fieldType ?? 'Text'}
              onChange={(e) => onChange({ ...item, fieldType: e.target.value as FieldType })}
            >
              {Object.entries(FIELD_TYPE_LABELS).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="block text-sm font-medium text-ink sm:col-span-2">
          {`Instructions for item ${position}`}
          <textarea
            className={inputClass}
            rows={2}
            disabled={disabled}
            maxLength={2000}
            value={item.instructions}
            onChange={(e) => onChange({ ...item, instructions: e.target.value })}
          />
        </label>
      </div>
      <div className="flex flex-wrap gap-4 text-sm">
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            disabled={disabled}
            checked={item.isRequired}
            onChange={(e) => onChange({ ...item, isRequired: e.target.checked })}
          />
          {`Item ${position} is required`}
        </label>
        {item.kind === 'FileUpload' && (
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              disabled={disabled}
              checked={item.allowMultiple}
              onChange={(e) => onChange({ ...item, allowMultiple: e.target.checked })}
            />
            {`Item ${position} allows multiple files`}
          </label>
        )}
      </div>
    </li>
  );
}

function TemplateEditor({ template }: { template: TemplateDetail | null }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { state } = useAuth();
  const canEdit = state.status === 'authenticated' && hasAnyRole(state.user, ['Admin', 'LoanOfficer']);
  const readOnly = !canEdit || (template?.isArchived ?? false);

  const [name, setName] = useState(template?.name ?? '');
  const [description, setDescription] = useState(template?.description ?? '');
  const [items, setItems] = useState<DraftItem[]>(() => (template ? template.items.map(toDraft) : []));

  const save = useMutation({
    mutationFn: () =>
      apiFetch<TemplateDetail>(template ? `/api/templates/${template.id}` : '/api/templates', {
        method: template ? 'PUT' : 'POST',
        body: {
          name,
          description,
          items: items.map(({ id, name: itemName, kind, instructions, allowMultiple, fieldType, isRequired }) => ({
            id,
            name: itemName,
            kind,
            instructions,
            allowMultiple: kind === 'FileUpload' && allowMultiple,
            fieldType: kind === 'FormField' ? fieldType : null,
            isRequired,
          })),
        },
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['templates'] });
      navigate('/templates');
    },
  });

  function update(index: number, item: DraftItem) {
    setItems((current) => current.map((existing, i) => (i === index ? item : existing)));
  }

  function move(index: number, direction: -1 | 1) {
    setItems((current) => {
      const next = [...current];
      [next[index], next[index + direction]] = [next[index + direction], next[index]];
      return next;
    });
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-6">
      <Card title="Details">
        <div className="mt-2 grid gap-3">
          <Field
            label="Template name"
            required
            disabled={readOnly}
            maxLength={200}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <label className="block text-sm font-medium text-ink">
            Description
            <textarea
              className={inputClass}
              rows={2}
              disabled={readOnly}
              maxLength={2000}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
            />
          </label>
        </div>
      </Card>

      <Card title="Items">
        {items.length === 0 && <p>This template has no items yet.</p>}
        <ol className="space-y-3" aria-label="Template items">
          {items.map((item, index) => (
            <ItemEditor
              key={item.key}
              item={item}
              index={index}
              count={items.length}
              disabled={readOnly}
              onChange={(changed) => update(index, changed)}
              onMove={(direction) => move(index, direction)}
              onRemove={() => setItems((current) => current.filter((_, i) => i !== index))}
            />
          ))}
        </ol>
        {!readOnly && (
          <div className="mt-3 flex flex-wrap gap-2">
            <button type="button" className={secondaryButtonClass} onClick={() => setItems([...items, blankItem('FileUpload')])}>
              Add file upload
            </button>
            <button type="button" className={secondaryButtonClass} onClick={() => setItems([...items, blankItem('FormField')])}>
              Add form field
            </button>
          </div>
        )}
      </Card>

      <ErrorList messages={messagesOf(save.error)} />
      <div className="flex flex-wrap gap-3">
        {!readOnly && (
          <button type="submit" disabled={save.isPending} className={primaryButtonClass}>
            Save template
          </button>
        )}
        <Link to="/templates" className={secondaryButtonClass}>
          Back to templates
        </Link>
      </div>
    </form>
  );
}

export function TemplateEditorPage() {
  const { id } = useParams();
  const template = useQuery({
    queryKey: ['templates', id],
    queryFn: () => apiFetch<TemplateDetail>(`/api/templates/${id}`),
    enabled: id !== undefined,
  });

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">{id ? 'Template' : 'New template'}</h1>
      {id === undefined && <TemplateEditor template={null} />}
      {id !== undefined && template.isPending && <p className="text-sm text-ink-muted">Loading template...</p>}
      {id !== undefined && template.isError && <ErrorList messages={messagesOf(template.error)} />}
      {template.data && <TemplateEditor key={template.dataUpdatedAt} template={template.data} />}
    </div>
  );
}
