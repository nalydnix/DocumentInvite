import { useQuery } from '@tanstack/react-query';
import { ErrorList, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import type { ItemFile, ReadUrl } from './requests';

/**
 * Previews one file inline through a short-lived read link. Every link requested is audited by the
 * server, so the link is fetched only while the preview is open and is never cached.
 */
export function FilePreview({ file, onClose }: { file: ItemFile; onClose: () => void }) {
  const link = useQuery({
    queryKey: ['file-preview', file.id],
    queryFn: () => apiFetch<ReadUrl>(`/api/files/${file.id}/preview-url`),
    gcTime: 0,
    staleTime: 0,
    refetchOnWindowFocus: false,
  });

  const isImage = file.contentType.startsWith('image/');
  const isPdf = file.contentType === 'application/pdf';

  return (
    <div
      role="dialog"
      aria-label={`Preview of ${file.fileName}`}
      className="space-y-2 rounded-card border border-surface-border bg-surface-muted p-3"
    >
      <div className="flex items-center justify-between gap-2">
        <p className="min-w-0 break-all text-sm font-medium text-ink">{file.fileName}</p>
        <button type="button" className={secondaryButtonClass} onClick={onClose}>
          Close preview
        </button>
      </div>
      {link.isPending && <p className="text-sm text-ink-muted">Loading preview...</p>}
      {link.isError && (
        <ErrorList messages={link.error instanceof ApiError ? link.error.messages : ['Could not load the preview.']} />
      )}
      {link.data && isPdf && (
        <iframe title={`Preview of ${file.fileName}`} src={link.data.url} className="h-[28rem] w-full rounded-lg bg-white" />
      )}
      {link.data && isImage && (
        <img src={link.data.url} alt={`Preview of ${file.fileName}`} className="max-h-[28rem] w-full rounded-lg bg-white object-contain" />
      )}
      {link.data && !isPdf && !isImage && (
        <p className="text-sm text-ink-muted">This file type cannot be previewed. Download it to view it.</p>
      )}
    </div>
  );
}
