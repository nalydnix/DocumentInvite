import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ErrorList } from '../components/Field';
import { MessageForm, MessageList, type ThreadEntry } from '../components/MessageThread';
import { listMessages, PortalApiError, postMessage, type PortalMessage } from './portalApi';

function toEntry(message: PortalMessage): ThreadEntry {
  return {
    id: message.id,
    authorType: message.authorType,
    authorLabel: message.authorType === 'Party' ? 'You' : message.authorType === 'Team' ? 'Your requester' : 'DocumentInvite',
    body: message.body,
    createdAt: message.createdAt,
  };
}

/** The message thread with the requester, in the portal. Posting needs the privacy notice to be accepted. */
export function PortalThread({
  privacyAccepted,
  onSessionEnded,
}: {
  privacyAccepted: boolean;
  onSessionEnded: () => void;
}) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const queryClient = useQueryClient();

  const thread = useQuery({
    queryKey: ['portal', 'messages', page, pageSize],
    queryFn: () => listMessages(page, pageSize),
    refetchOnWindowFocus: true,
  });
  useEffect(() => {
    if (thread.error instanceof PortalApiError && thread.error.code === 'session_ended') onSessionEnded();
  }, [thread.error, onSessionEnded]);

  const post = useMutation({
    mutationFn: postMessage,
    onSuccess: async () => {
      setPage(1);
      await queryClient.invalidateQueries({ queryKey: ['portal', 'messages'] });
    },
  });

  return (
    <section
      aria-labelledby="messages-heading"
      className="space-y-3 rounded-card border border-surface-border bg-surface p-4 shadow-card"
    >
      <h2 id="messages-heading" className="text-base font-semibold text-ink">
        Messages
      </h2>
      {privacyAccepted ? (
        <>
          <MessageForm
            label="Message your requester"
            submitLabel="Send message"
            placeholder="Ask a question or add a note for your requester."
            onSubmit={async (body) => {
              await post.mutateAsync(body);
            }}
          />
          <ErrorList messages={post.error instanceof PortalApiError ? post.error.messages : post.error ? ['Your message was not sent. Please try again.'] : []} />
        </>
      ) : (
        <p className="text-sm text-ink-muted">Accept the privacy notice above to send a message.</p>
      )}
      <MessageList
        label="Messages, newest first"
        viewer="party"
        data={thread.data}
        entries={(thread.data?.items ?? []).map(toEntry)}
        loading={thread.isPending}
        error={thread.isError ? 'Your messages could not be loaded.' : null}
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
  );
}
