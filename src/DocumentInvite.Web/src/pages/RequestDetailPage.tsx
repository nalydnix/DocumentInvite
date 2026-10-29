import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { ErrorList, inputClass, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { StatusBadge } from '../components/StatusBadge';
import { ApiError, apiDownload, apiFetch } from '../lib/api';
import { partyLabel } from './contacts';
import { formatAmount, formatDate, REQUEST_WRITER_ROLES, type Invite, type Request } from './requests';
import { InviteThread } from './InviteThread';
import { ItemReview } from './ItemReview';
import { SendInvitesPanel } from './SendInvitesPanel';

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function InviteRow({ invite, canWrite, locked }: { invite: Invite; canWrite: boolean; locked: boolean }) {
  const queryClient = useQueryClient();
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['request', invite.requestId] });
    await queryClient.invalidateQueries({ queryKey: ['requests'] });
  };
  const revoke = useMutation({
    mutationFn: () => apiFetch<Invite>(`/api/invites/${invite.id}/revoke`, { method: 'POST' }),
    onSuccess: refresh,
  });
  const reissue = useMutation({
    mutationFn: () => apiFetch<Invite>(`/api/invites/${invite.id}/reissue`, { method: 'POST' }),
    onSuccess: refresh,
  });

  const approved = invite.items.filter((item) => item.status === 'Approved').length;
  const revoked = invite.revokedAt !== null;

  return (
    <li className="space-y-2 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="font-medium text-ink">{invite.partyName}</p>
          <p className="break-all text-sm text-ink-muted">{invite.partyEmail}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="rounded-full bg-brand-50 px-2 py-0.5 text-xs font-medium text-brand-700">
            {partyLabel(invite.partyType)}
          </span>
          {invite.hasPassword && (
            <span className="rounded-full bg-surface-muted px-2 py-0.5 text-xs font-medium text-ink-muted">
              Password protected
            </span>
          )}
          {revoked ? (
            <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs font-medium text-red-700">Link revoked</span>
          ) : (
            <StatusBadge status={invite.status} />
          )}
        </div>
      </div>
      <p className="text-sm text-ink-muted">
        {approved} of {invite.items.length} items approved &middot; due {formatDate(invite.dueDate)} &middot; reminder
        every {invite.reminderIntervalDays} days &middot; link expires {formatDate(invite.tokenExpiresAt)}
      </p>
      {canWrite && (
        <div className="flex flex-wrap gap-2">
          {!revoked && (
            <button
              type="button"
              className={secondaryButtonClass}
              disabled={revoke.isPending || locked}
              aria-label={`Revoke link for ${invite.partyName}`}
              onClick={() => revoke.mutate()}
            >
              Revoke link
            </button>
          )}
          {invite.status !== 'Completed' && (
            <button
              type="button"
              className={secondaryButtonClass}
              disabled={reissue.isPending || locked}
              aria-label={`Reissue link for ${invite.partyName}`}
              onClick={() => reissue.mutate()}
            >
              Reissue link
            </button>
          )}
        </div>
      )}
      <ul className="space-y-3 rounded-lg bg-surface-muted p-3" aria-label={`Items for ${invite.partyName}`}>
        {invite.items.map((item) => (
          <ItemReview key={item.id} item={item} requestId={invite.requestId} canWrite={canWrite} locked={locked} />
        ))}
      </ul>
      <InviteThread inviteId={invite.id} partyName={invite.partyName} canWrite={canWrite} />
      <ErrorList messages={[...messagesOf(revoke.error), ...messagesOf(reissue.error)]} />
      {reissue.isSuccess && <p role="status" className="text-sm text-ink-muted">A new link was emailed. The old link no longer works.</p>}
    </li>
  );
}

/** Complete and reopen actions for the request, with the API's refusals shown to the user. */
function CompletionPanel({ request }: { request: Request }) {
  const queryClient = useQueryClient();
  const [reopening, setReopening] = useState(false);
  const [reason, setReason] = useState('');
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['request', request.id] });
    await queryClient.invalidateQueries({ queryKey: ['requests'] });
  };
  const complete = useMutation({
    mutationFn: () => apiFetch(`/api/requests/${request.id}/complete`, { method: 'POST' }),
    onSuccess: refresh,
  });
  const reopen = useMutation({
    mutationFn: (text: string) => apiFetch(`/api/requests/${request.id}/reopen`, { method: 'POST', body: { reason: text } }),
    onSuccess: async () => {
      setReopening(false);
      setReason('');
      await refresh();
    },
  });
  const isCompleted = request.status === 'Completed';

  const submitReopen = (event: FormEvent) => {
    event.preventDefault();
    reopen.mutate(reason.trim());
  };

  return (
    <section className="space-y-3" aria-label="Request completion">
      {isCompleted ? (
        <>
          <p role="status" className="rounded-lg bg-green-50 p-3 text-sm text-green-700">
            This request is completed and locked. Items cannot be approved or rejected and invites cannot be sent or
            revoked until it is reopened.
          </p>
          {!reopening && (
            <button type="button" className={secondaryButtonClass} onClick={() => setReopening(true)}>
              Reopen request
            </button>
          )}
        </>
      ) : (
        <div className="space-y-1">
          <button
            type="button"
            className={primaryButtonClass}
            disabled={request.status !== 'ReadyToComplete' || complete.isPending}
            onClick={() => complete.mutate()}
          >
            {complete.isPending ? 'Completing...' : 'Complete request'}
          </button>
          {request.status !== 'ReadyToComplete' && (
            <p className="text-xs text-ink-muted">Available once every item is approved.</p>
          )}
        </div>
      )}
      {reopening && (
        <form onSubmit={submitReopen} className="space-y-2">
          <label className="block text-sm font-medium text-ink">
            Reason for reopening this request
            <textarea
              className={inputClass}
              rows={3}
              maxLength={1000}
              required
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </label>
          <p className="text-xs text-ink-muted">The reason is posted to every party's message thread.</p>
          <div className="flex gap-2">
            <button type="submit" className={primaryButtonClass} disabled={reopen.isPending || reason.trim() === ''}>
              Reopen
            </button>
            <button
              type="button"
              className={secondaryButtonClass}
              onClick={() => {
                setReopening(false);
                reopen.reset();
              }}
            >
              Cancel
            </button>
          </div>
        </form>
      )}
      <ErrorList messages={[...messagesOf(complete.error), ...messagesOf(reopen.error)]} />
    </section>
  );
}

export function RequestDetailPage() {
  const { id } = useParams();
  const exportApproved = useMutation({
    mutationFn: async (reference: string) => {
      const blob = await apiDownload(`/api/requests/${id}/export`);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${reference}-approved.zip`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    },
  });
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, REQUEST_WRITER_ROLES);
  const request = useQuery({ queryKey: ['request', id], queryFn: () => apiFetch<Request>(`/api/requests/${id}`) });

  if (request.isPending) return <p className="text-sm text-ink-muted">Loading request...</p>;
  if (request.isError) {
    return (
      <ErrorList
        messages={
          request.error instanceof ApiError && request.error.status === 404
            ? ['This request does not exist.']
            : messagesOf(request.error)
        }
      />
    );
  }

  const data = request.data;
  const locked = data.status === 'Completed';
  return (
    <div className="space-y-6">
      <div className="space-y-1">
        <Link to="/requests" className="text-sm text-brand-700 hover:underline">
          Back to requests
        </Link>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-xl font-semibold text-ink">
            {data.reference} &middot; {data.name}
          </h1>
          <div className="flex flex-wrap items-center gap-3">
            <StatusBadge status={data.status} />
            {canWrite && (
              <button
                type="button"
                className={primaryButtonClass}
                disabled={exportApproved.isPending}
                onClick={() => exportApproved.mutate(data.reference)}
              >
                {exportApproved.isPending ? 'Preparing zip...' : 'Download approved'}
              </button>
            )}
          </div>
        </div>
        <p className="text-sm text-ink-muted">
          {data.requestType} &middot; {formatAmount(data.amount)}
        </p>
        <ErrorList messages={messagesOf(exportApproved.error)} />
      </div>

      {canWrite && <CompletionPanel request={data} />}

      <section className="space-y-3">
        <h2 className="text-base font-semibold text-ink">Parties</h2>
        {data.invites.length === 0 ? (
          <p className="text-sm text-ink-muted">No parties have been invited yet.</p>
        ) : (
          <ul className="space-y-3" aria-label="Parties">
            {data.invites.map((invite) => (
              <InviteRow key={invite.id} invite={invite} canWrite={canWrite} locked={locked} />
            ))}
          </ul>
        )}
      </section>

      {canWrite && !locked && <SendInvitesPanel requestId={data.id} existingInvites={data.invites} />}
    </div>
  );
}
