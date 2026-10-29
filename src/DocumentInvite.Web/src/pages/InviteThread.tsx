import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ErrorList, secondaryButtonClass } from '../components/Field';
import { MessageForm, MessageList, type ThreadEntry } from '../components/MessageThread';
import { ApiError, apiFetch } from '../lib/api';
import type { PagedResponse } from '../lib/paging';

export interface StaffMessage {
  id: string;
  authorType: 'Team' | 'Party' | 'System';
  authorName: string | null;
  body: string;
  isInternal: boolean;
  createdAt: string;
}

type Mode = 'message' | 'note';

function toEntry(message: StaffMessage, partyName: string): ThreadEntry {
  return {
    id: message.id,
    authorType: message.authorType,
    authorLabel:
      message.authorType === 'Party' ? partyName : message.authorType === 'System' ? 'System' : (message.authorName ?? 'Team'),
    body: message.body,
    isInternal: message.isInternal,
    createdAt: message.createdAt,
  };
}

/**
 * The thread with one party, on the request page. It is closed until opened, so a request with many parties
 * does not load every thread at once. Only staff who can write see the box; the API enforces the same.
 */
export function InviteThread({
  inviteId,
  partyName,
  canWrite,
}: {
  inviteId: string;
  partyName: string;
  canWrite: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [mode, setMode] = useState<Mode>('message');
  const queryClient = useQueryClient();
  const key = ['messages', inviteId] as const;

  const thread = useQuery({
    queryKey: [...key, page, pageSize],
    queryFn: () =>
      apiFetch<PagedResponse<StaffMessage>>(`/api/invites/${inviteId}/messages?page=${page}&pageSize=${pageSize}`),
    enabled: open,
  });

  const post = useMutation({
    mutationFn: ({ body, kind }: { body: string; kind: Mode }) =>
      apiFetch<StaffMessage>(`/api/invites/${inviteId}/${kind === 'note' ? 'notes' : 'messages'}`, {
        method: 'POST',
        body: { body },
      }),
    onSuccess: async () => {
      setPage(1);
      await queryClient.invalidateQueries({ queryKey: key });
    },
  });

  const panelId = `thread-${inviteId}`;
  const errors = post.error instanceof ApiError ? post.error.messages : post.error ? ['Something went wrong. Please try again.'] : [];

  return (
    <div className="space-y-3">
      <button
        type="button"
        className={secondaryButtonClass}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((value) => !value)}
      >
        {open ? 'Hide' : 'Show'} messages with {partyName}
      </button>
      {open && (
        <section id={panelId} aria-label={`Messages with ${partyName}`} className="space-y-3 rounded-lg bg-surface-muted p-3">
          {canWrite && (
            <>
              <MessageForm
                label={mode === 'note' ? 'Internal note' : `Message to ${partyName}`}
                submitLabel={mode === 'note' ? 'Add note' : 'Send message'}
                placeholder={
                  mode === 'note'
                    ? 'Only your team can see this.'
                    : `${partyName} is emailed a link and sees this in their portal.`
                }
                onSubmit={async (body) => {
                  await post.mutateAsync({ body, kind: mode });
                }}
                extra={
                  <fieldset className="flex flex-wrap gap-4 text-sm text-ink">
                    <legend className="sr-only">Type of entry</legend>
                    <label className="flex min-h-11 items-center gap-2 sm:min-h-0">
                      <input type="radio" name={`mode-${inviteId}`} checked={mode === 'message'} onChange={() => setMode('message')} />
                      Message to party
                    </label>
                    <label className="flex min-h-11 items-center gap-2 sm:min-h-0">
                      <input type="radio" name={`mode-${inviteId}`} checked={mode === 'note'} onChange={() => setMode('note')} />
                      Internal note (team only)
                    </label>
                  </fieldset>
                }
              />
              <ErrorList messages={errors} />
            </>
          )}
          {!canWrite && <p className="text-xs text-ink-muted">You can read this thread but not post to it.</p>}
          <MessageList
            label={`Thread with ${partyName}, newest first`}
            viewer="staff"
            data={thread.data}
            entries={(thread.data?.items ?? []).map((message) => toEntry(message, partyName))}
            loading={thread.isPending}
            error={thread.isError ? 'The messages could not be loaded.' : null}
            page={page}
            pageSize={pageSize}
            onPageChange={setPage}
            onPageSizeChange={(size) => {
              setPageSize(size);
              setPage(1);
            }}
            emptyText="No messages yet."
          />
        </section>
      )}
    </div>
  );
}
