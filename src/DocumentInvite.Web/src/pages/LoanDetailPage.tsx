import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { ErrorList, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { StatusBadge } from '../components/StatusBadge';
import { ApiError, apiDownload, apiFetch } from '../lib/api';
import { partyLabel } from './contacts';
import { formatAmount, formatDate, LOAN_WRITER_ROLES, type Invite, type Loan } from './loans';
import { ItemReview } from './ItemReview';
import { SendInvitesPanel } from './SendInvitesPanel';

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function InviteRow({ invite, canWrite }: { invite: Invite; canWrite: boolean }) {
  const queryClient = useQueryClient();
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['loan', invite.loanId] });
    await queryClient.invalidateQueries({ queryKey: ['loans'] });
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
              disabled={revoke.isPending}
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
              disabled={reissue.isPending}
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
          <ItemReview key={item.id} item={item} loanId={invite.loanId} canWrite={canWrite} />
        ))}
      </ul>
      <ErrorList messages={[...messagesOf(revoke.error), ...messagesOf(reissue.error)]} />
      {reissue.isSuccess && <p role="status" className="text-sm text-ink-muted">A new link was emailed. The old link no longer works.</p>}
    </li>
  );
}

export function LoanDetailPage() {
  const { id } = useParams();
  const exportApproved = useMutation({
    mutationFn: async (reference: string) => {
      const blob = await apiDownload(`/api/loans/${id}/export`);
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
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, LOAN_WRITER_ROLES);
  const loan = useQuery({ queryKey: ['loan', id], queryFn: () => apiFetch<Loan>(`/api/loans/${id}`) });

  if (loan.isPending) return <p className="text-sm text-ink-muted">Loading loan...</p>;
  if (loan.isError) {
    return (
      <ErrorList
        messages={
          loan.error instanceof ApiError && loan.error.status === 404
            ? ['This loan does not exist.']
            : messagesOf(loan.error)
        }
      />
    );
  }

  const data = loan.data;
  return (
    <div className="space-y-6">
      <div className="space-y-1">
        <Link to="/loans" className="text-sm text-brand-700 hover:underline">
          Back to loans
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
          {data.loanType} &middot; {formatAmount(data.amount)}
        </p>
        <ErrorList messages={messagesOf(exportApproved.error)} />
      </div>

      <section className="space-y-3">
        <h2 className="text-base font-semibold text-ink">Parties</h2>
        {data.invites.length === 0 ? (
          <p className="text-sm text-ink-muted">No parties have been invited yet.</p>
        ) : (
          <ul className="space-y-3" aria-label="Parties">
            {data.invites.map((invite) => (
              <InviteRow key={invite.id} invite={invite} canWrite={canWrite} />
            ))}
          </ul>
        )}
      </section>

      {canWrite && <SendInvitesPanel loanId={data.id} existingInvites={data.invites} />}
    </div>
  );
}
