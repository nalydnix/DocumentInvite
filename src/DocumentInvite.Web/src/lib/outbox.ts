import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from './api';

/** How often the unread badge is refreshed while the app is open. */
const UNREAD_POLL_MS = 60_000;

/** The signed-in user's unread outbox count. Pass false to skip the request (e.g. for roles without outbox access). */
export function useUnreadOutboxCount(enabled: boolean): number {
  const query = useQuery({
    queryKey: ['outbox', 'unread-count'],
    queryFn: () => apiFetch<{ count: number }>('/api/outbox/unread-count'),
    enabled,
    refetchInterval: UNREAD_POLL_MS,
  });
  return enabled ? (query.data?.count ?? 0) : 0;
}

/** Marks an outbox email read or unread, then refreshes the list, the open email and the badge. */
export function useSetOutboxRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, read }: { id: string; read: boolean }) =>
      apiFetch<void>(`/api/outbox/${id}/${read ? 'read' : 'unread'}`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['outbox'] }),
  });
}
