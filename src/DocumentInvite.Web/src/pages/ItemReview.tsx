import { useState, type FormEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ErrorList, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { FilePreview } from './FilePreview';
import { formatBytes, type InviteItem, type ItemFile, type ReadUrl, type RequestItemStatus } from './loans';

const ITEM_STATUS_STYLES: Record<RequestItemStatus, string> = {
  Outstanding: 'bg-surface-muted text-ink-muted',
  Submitted: 'bg-brand-50 text-brand-700',
  Approved: 'bg-green-50 text-green-700',
  Rejected: 'bg-red-50 text-red-700',
};

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function FileRow({ file, canWrite, label }: { file: ItemFile; canWrite: boolean; label: string }) {
  const [previewing, setPreviewing] = useState(false);
  const download = useMutation({
    mutationFn: () => apiFetch<ReadUrl>(`/api/files/${file.id}/download-url`),
    onSuccess: (link) => {
      window.location.assign(link.url);
    },
  });

  return (
    <li className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="min-w-0 break-all text-sm text-ink">
          {file.fileName} <span className="text-ink-muted">&middot; {formatBytes(file.sizeBytes)}</span>
        </p>
        <div className="flex gap-2">
          <button
            type="button"
            className={secondaryButtonClass}
            aria-label={`Preview ${file.fileName} for ${label}`}
            onClick={() => setPreviewing((open) => !open)}
          >
            Preview
          </button>
          {canWrite && (
            <button
              type="button"
              className={secondaryButtonClass}
              aria-label={`Download ${file.fileName} for ${label}`}
              disabled={download.isPending}
              onClick={() => download.mutate()}
            >
              Download
            </button>
          )}
        </div>
      </div>
      <ErrorList messages={messagesOf(download.error)} />
      {previewing && <FilePreview file={file} onClose={() => setPreviewing(false)} />}
    </li>
  );
}

/** One requested item: its status, answers and files, with approve and reject for those who may review. */
export function ItemReview({ item, loanId, canWrite }: { item: InviteItem; loanId: string; canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [rejecting, setRejecting] = useState(false);
  const [reason, setReason] = useState('');

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['loan', loanId] });
    await queryClient.invalidateQueries({ queryKey: ['loans'] });
  };
  const approve = useMutation({
    mutationFn: () => apiFetch(`/api/items/${item.id}/approve`, { method: 'POST' }),
    onSuccess: refresh,
  });
  const reject = useMutation({
    mutationFn: (text: string) =>
      apiFetch(`/api/items/${item.id}/reject`, { method: 'POST', body: { reason: text } }),
    onSuccess: async () => {
      setRejecting(false);
      setReason('');
      await refresh();
    },
  });

  const canApprove = canWrite && item.status === 'Submitted';
  const canReject = canWrite && (item.status === 'Submitted' || item.status === 'Approved');

  const submitReject = (event: FormEvent) => {
    event.preventDefault();
    reject.mutate(reason.trim());
  };

  return (
    <li className="space-y-2 border-t border-surface-border pt-3 first:border-t-0 first:pt-0">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-medium text-ink">
          {item.name}
          {!item.isRequired && <span className="font-normal text-ink-muted"> (optional)</span>}
        </p>
        <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${ITEM_STATUS_STYLES[item.status]}`}>
          {item.status}
        </span>
      </div>

      {item.kind === 'FormField' && item.fieldValue !== null && (
        <p className="text-sm text-ink">
          <span className="text-ink-muted">Answer:</span> {item.fieldValue}
        </p>
      )}
      {item.partyNote && (
        <p className="text-sm text-ink">
          <span className="text-ink-muted">Note from party:</span> {item.partyNote}
        </p>
      )}
      {item.status === 'Rejected' && item.rejectionReason && (
        <p className="text-sm text-red-700">Rejected: {item.rejectionReason}</p>
      )}

      {item.files.length > 0 && (
        <ul className="space-y-2" aria-label={`Files for ${item.name}`}>
          {item.files.map((file) => (
            <FileRow key={file.id} file={file} canWrite={canWrite} label={item.name} />
          ))}
        </ul>
      )}

      {(canApprove || canReject) && !rejecting && (
        <div className="flex flex-wrap gap-2">
          {canApprove && (
            <button
              type="button"
              className={primaryButtonClass}
              disabled={approve.isPending}
              aria-label={`Approve ${item.name}`}
              onClick={() => approve.mutate()}
            >
              Approve
            </button>
          )}
          {canReject && (
            <button
              type="button"
              className={secondaryButtonClass}
              aria-label={`Reject ${item.name}`}
              onClick={() => setRejecting(true)}
            >
              Reject
            </button>
          )}
        </div>
      )}

      {rejecting && (
        <form onSubmit={submitReject} className="space-y-2">
          <label className="block text-sm font-medium text-ink">
            Reason for rejecting {item.name}
            <textarea
              className={inputClass}
              rows={3}
              maxLength={1000}
              required
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </label>
          <p className="text-xs text-ink-muted">The party sees this reason and is emailed a link to resubmit.</p>
          <div className="flex gap-2">
            <button type="submit" className={primaryButtonClass} disabled={reject.isPending || reason.trim() === ''}>
              Send rejection
            </button>
            <button
              type="button"
              className={secondaryButtonClass}
              onClick={() => {
                setRejecting(false);
                reject.reset();
              }}
            >
              Cancel
            </button>
          </div>
        </form>
      )}
      <ErrorList messages={[...messagesOf(approve.error), ...messagesOf(reject.error)]} />
    </li>
  );
}
