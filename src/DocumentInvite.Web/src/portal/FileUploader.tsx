import { useRef, useState, type DragEvent } from 'react';
import {
  ACCEPT,
  ALLOWED_LABEL,
  fileProblem,
  PortalApiError,
  uploadFile,
  type PortalFile,
  type UploadFunction,
} from './portalApi';

interface UploadRow {
  key: number;
  name: string;
  fraction: number;
  state: 'uploading' | 'processing' | 'done' | 'error';
  error?: string;
}

interface Props {
  itemId: string;
  itemName: string;
  allowMultiple: boolean;
  disabled?: boolean;
  onUploaded: (file: PortalFile) => void;
  /** Replaceable for tests. */
  upload?: UploadFunction;
}

const buttonClass =
  'inline-flex min-h-11 items-center justify-center rounded-lg border border-surface-border bg-surface px-4 py-2 ' +
  'text-sm font-medium text-ink hover:bg-surface-muted focus-within:ring-2 focus-within:ring-brand-200';

/** Drag-and-drop or tap-to-upload (including the camera on phones). Each file shows its own progress. */
export function FileUploader({ itemId, itemName, allowMultiple, disabled = false, onUploaded, upload = uploadFile }: Props) {
  const [rows, setRows] = useState<UploadRow[]>([]);
  const [problems, setProblems] = useState<string[]>([]);
  const [dragging, setDragging] = useState(false);
  const counter = useRef(0);

  const patch = (key: number, change: Partial<UploadRow>) =>
    setRows((current) => current.map((row) => (row.key === key ? { ...row, ...change } : row)));

  async function send(file: File) {
    const key = (counter.current += 1);
    setRows((current) => [...current, { key, name: file.name, fraction: 0, state: 'uploading' }]);
    try {
      const done = await upload(itemId, file, (progress) =>
        patch(key, { fraction: progress.fraction, state: progress.processing ? 'processing' : 'uploading' }),
      );
      patch(key, { fraction: 1, state: 'done' });
      onUploaded(done);
    } catch (error) {
      patch(key, {
        state: 'error',
        error: error instanceof PortalApiError ? error.message : 'The upload did not finish. Please try again.',
      });
    }
  }

  function accept(list: FileList | File[] | null) {
    const files = Array.from(list ?? []);
    if (files.length === 0 || disabled) return;
    const found: string[] = [];
    if (!allowMultiple && files.length > 1) found.push('This item takes one file. Only the first file was used.');
    const chosen = allowMultiple ? files : files.slice(0, 1);
    for (const file of chosen) {
      const problem = fileProblem(file);
      if (problem) found.push(problem);
      else void send(file);
    }
    setProblems(found);
  }

  function handleDrop(event: DragEvent) {
    event.preventDefault();
    setDragging(false);
    accept(event.dataTransfer.files);
  }

  const inputId = `file-${itemId}`;
  return (
    <div className="space-y-2">
      <div
        data-testid={`dropzone-${itemId}`}
        onDragOver={(event) => {
          event.preventDefault();
          if (!disabled) setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        className={`rounded-lg border-2 border-dashed p-4 text-center text-sm ${
          dragging ? 'border-brand-500 bg-brand-50' : 'border-surface-border bg-surface-muted'
        } ${disabled ? 'opacity-60' : ''}`}
      >
        <p className="hidden text-ink-muted sm:block">Drag and drop files here, or</p>
        <div className="mt-2 flex flex-wrap items-center justify-center gap-2 sm:mt-1">
          <label htmlFor={inputId} className={`${buttonClass} ${disabled ? 'pointer-events-none' : 'cursor-pointer'}`}>
            {allowMultiple ? 'Choose files' : 'Choose a file'}{' '}
            <span className="sr-only">for {itemName}</span>
            <input
              id={inputId}
              type="file"
              className="sr-only"
              accept={ACCEPT}
              multiple={allowMultiple}
              disabled={disabled}
              onChange={(event) => {
                accept(event.target.files);
                event.target.value = '';
              }}
            />
          </label>
          <label htmlFor={`${inputId}-camera`} className={`${buttonClass} sm:hidden ${disabled ? 'pointer-events-none' : 'cursor-pointer'}`}>
            Take a photo{' '}
            <span className="sr-only">for {itemName}</span>
            <input
              id={`${inputId}-camera`}
              type="file"
              className="sr-only"
              accept="image/jpeg,image/png"
              capture="environment"
              disabled={disabled}
              onChange={(event) => {
                accept(event.target.files);
                event.target.value = '';
              }}
            />
          </label>
        </div>
        <p className="mt-2 text-xs text-ink-muted">{ALLOWED_LABEL}</p>
      </div>

      {problems.length > 0 && (
        <ul role="alert" className="space-y-1 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {problems.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      )}

      {rows.length > 0 && (
        <ul className="space-y-2" aria-label={`Uploads for ${itemName}`}>
          {rows.map((row) => (
            <li key={row.key} className="rounded-lg border border-surface-border bg-surface p-3 text-sm">
              <div className="flex items-center justify-between gap-2">
                <span className="min-w-0 break-all font-medium text-ink">{row.name}</span>
                <span className="shrink-0 text-xs text-ink-muted">
                  {row.state === 'uploading' && `${Math.round(row.fraction * 100)}%`}
                  {row.state === 'processing' && 'Checking...'}
                  {row.state === 'done' && 'Uploaded'}
                  {row.state === 'error' && 'Failed'}
                </span>
              </div>
              {(row.state === 'uploading' || row.state === 'processing') && (
                <div
                  role="progressbar"
                  aria-label={`Upload of ${row.name}`}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={Math.round(row.fraction * 100)}
                  className="mt-2 h-1.5 overflow-hidden rounded-full bg-surface-muted"
                >
                  <div className="h-full bg-brand-600 transition-all" style={{ width: `${row.fraction * 100}%` }} />
                </div>
              )}
              {row.state === 'error' && <p className="mt-1 text-red-700">{row.error}</p>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
