import { useState, type FormEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { ErrorList, inputClass, primaryButtonClass } from '../components/Field';
import { FileUploader } from './FileUploader';
import { PortalApiError, saveField, saveNote, type ItemStatus, type PortalItem, type UploadFunction } from './portalApi';

const STATUS_LABEL: Record<ItemStatus, string> = {
  Outstanding: 'To do',
  Submitted: 'Submitted',
  Approved: 'Approved',
  Rejected: 'Needs attention',
};

const STATUS_STYLE: Record<ItemStatus, string> = {
  Outstanding: 'bg-surface-muted text-ink-muted',
  Submitted: 'bg-brand-50 text-brand-700',
  Approved: 'bg-green-50 text-green-700',
  Rejected: 'bg-red-50 text-red-700',
};

function messagesOf(error: unknown): string[] {
  return error instanceof PortalApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function FieldEditor({ item, disabled, onSaved }: { item: PortalItem; disabled: boolean; onSaved: () => void }) {
  const [value, setValue] = useState(item.fieldValue ?? '');
  const save = useMutation({ mutationFn: (v: string) => saveField(item.id, v), onSuccess: onSaved });
  const label = `Answer for ${item.name}`;

  function submit(event: FormEvent) {
    event.preventDefault();
    save.mutate(value);
  }

  return (
    <form onSubmit={submit} className="space-y-2">
      {item.fieldType === 'YesNo' ? (
        <fieldset className="flex gap-4" disabled={disabled}>
          <legend className="sr-only">{label}</legend>
          {['Yes', 'No'].map((option) => (
            <label key={option} className="flex min-h-11 items-center gap-2 text-sm text-ink">
              <input
                type="radio"
                name={`field-${item.id}`}
                value={option}
                checked={value === option}
                onChange={() => setValue(option)}
              />
              {option}
            </label>
          ))}
        </fieldset>
      ) : (
        <input
          aria-label={label}
          className={`${inputClass} !mt-0`}
          type={item.fieldType === 'Number' ? 'number' : item.fieldType === 'Date' ? 'date' : 'text'}
          step={item.fieldType === 'Number' ? 'any' : undefined}
          inputMode={item.fieldType === 'Number' ? 'decimal' : undefined}
          maxLength={2000}
          disabled={disabled}
          value={value}
          onChange={(event) => setValue(event.target.value)}
        />
      )}
      <button type="submit" className={primaryButtonClass} disabled={disabled || save.isPending}>
        {save.isPending ? 'Saving...' : 'Save answer'}{' '}
        <span className="sr-only">for {item.name}</span>
      </button>
      {save.isSuccess && !save.isPending && <span role="status" className="ml-2 text-sm text-green-700">Saved</span>}
      <ErrorList messages={messagesOf(save.error)} />
    </form>
  );
}

function NoteEditor({ item, disabled, onSaved }: { item: PortalItem; disabled: boolean; onSaved: () => void }) {
  const [open, setOpen] = useState(Boolean(item.note));
  const [note, setNote] = useState(item.note ?? '');
  const save = useMutation({ mutationFn: (n: string) => saveNote(item.id, n), onSuccess: onSaved });

  if (!open) {
    return (
      <button
        type="button"
        className="min-h-11 text-sm font-medium text-brand-700 hover:underline disabled:opacity-60"
        disabled={disabled}
        onClick={() => setOpen(true)}
      >
        Add a note <span className="sr-only">to {item.name}</span>
      </button>
    );
  }

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate(note);
      }}
      className="space-y-2"
    >
      <label className="block text-sm font-medium text-ink">
        Note for {item.name}
        <textarea
          className={inputClass}
          rows={2}
          maxLength={2000}
          disabled={disabled}
          value={note}
          onChange={(event) => setNote(event.target.value)}
        />
      </label>
      <button type="submit" className="min-h-11 text-sm font-medium text-brand-700 hover:underline disabled:opacity-60" disabled={disabled || save.isPending}>
        {save.isPending ? 'Saving...' : 'Save note'}
      </button>
      {save.isSuccess && !save.isPending && <span role="status" className="ml-2 text-sm text-green-700">Note saved</span>}
      <ErrorList messages={messagesOf(save.error)} />
    </form>
  );
}

interface Props {
  item: PortalItem;
  /** Uploads, answers and notes stay disabled until the privacy notice is accepted. */
  privacyAccepted: boolean;
  onChanged: () => void;
  upload?: UploadFunction;
}

/** One checklist item: instructions, status, rejection reason, the files or answer, and the party's note. */
export function ItemCard({ item, privacyAccepted, onChanged, upload }: Props) {
  const locked = item.status === 'Approved';
  const disabled = locked || !privacyAccepted;

  return (
    <li
      aria-label={item.name}
      className="space-y-3 rounded-card border border-surface-border bg-surface p-4 shadow-card"
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="break-words text-base font-semibold text-ink">{item.name}</h3>
          <p className="text-xs text-ink-muted">{item.isRequired ? 'Required' : 'Optional'}</p>
        </div>
        <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${STATUS_STYLE[item.status]}`}>
          {STATUS_LABEL[item.status]}
        </span>
      </div>

      {item.instructions && <p className="whitespace-pre-line break-words text-sm text-ink-muted">{item.instructions}</p>}

      {item.status === 'Rejected' && (
        <div role="alert" className="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          <p className="font-medium">Please fix this item</p>
          <p className="break-words">{item.rejectionReason ?? 'Your lender asked for this item again.'}</p>
        </div>
      )}

      {item.kind === 'FileUpload' ? (
        <div className="space-y-2">
          {item.files.length > 0 && (
            <ul className="space-y-1" aria-label={`Files for ${item.name}`}>
              {item.files.map((file) => (
                <li key={file.id} className="flex items-center justify-between gap-2 rounded-lg bg-surface-muted px-3 py-2 text-sm">
                  <span className="min-w-0 break-all text-ink">{file.fileName}</span>
                  <span className="shrink-0 text-xs text-ink-muted">{formatSize(file.sizeBytes)}</span>
                </li>
              ))}
            </ul>
          )}
          {!locked && (
            <FileUploader
              itemId={item.id}
              itemName={item.name}
              allowMultiple={item.allowMultiple}
              disabled={disabled}
              onUploaded={onChanged}
              upload={upload}
            />
          )}
        </div>
      ) : locked ? (
        <p className="text-sm text-ink">{item.fieldValue}</p>
      ) : (
        <FieldEditor item={item} disabled={disabled} onSaved={onChanged} />
      )}

      {!locked && <NoteEditor item={item} disabled={disabled} onSaved={onChanged} />}
    </li>
  );
}
