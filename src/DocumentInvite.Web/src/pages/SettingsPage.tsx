import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '../components/Card';
import { ErrorList, Field, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { Pagination } from '../components/Pagination';
import { useClampPage, usePaging, type PagedResponse } from '../lib/paging';
import { useDemoConfig } from './demo';

interface Settings {
  defaultReminderIntervalDays: number;
  retentionDaysAfterCompletion: number;
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function SettingsForm({ settings }: { settings: Settings }) {
  const queryClient = useQueryClient();
  const [reminder, setReminder] = useState(String(settings.defaultReminderIntervalDays));
  const [retention, setRetention] = useState(String(settings.retentionDaysAfterCompletion));

  const save = useMutation({
    mutationFn: () =>
      apiFetch<Settings>('/api/settings', {
        method: 'PUT',
        body: {
          defaultReminderIntervalDays: Number(reminder),
          retentionDaysAfterCompletion: Number(retention),
        },
      }),
    onSuccess: (saved) => queryClient.setQueryData(['settings'], saved),
  });

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  return (
    <form onSubmit={handleSubmit} className="mt-2 grid gap-3 sm:grid-cols-2">
      <Field
        label="Default reminder interval (days)"
        type="number"
        min={1}
        max={90}
        required
        value={reminder}
        onChange={(e) => setReminder(e.target.value)}
      />
      <Field
        label="Retention after completion (days)"
        type="number"
        min={30}
        max={3650}
        required
        value={retention}
        onChange={(e) => setRetention(e.target.value)}
      />
      <div className="space-y-3 sm:col-span-2">
        <ErrorList messages={messagesOf(save.error)} />
        {save.isSuccess && (
          <p role="status" className="text-sm text-success-700">
            Settings saved.
          </p>
        )}
        <button type="submit" disabled={save.isPending} className={primaryButtonClass}>
          Save settings
        </button>
      </div>
    </form>
  );
}

interface ReminderRun {
  windowKey: string;
  alreadyRan: boolean;
  invitesDue: number;
  remindersSent: number;
  overdueNoticesSent: number;
  failed: number;
}

function plural(count: number, word: string) {
  return `${count} ${word}${count === 1 ? '' : 's'}`;
}

function RunRemindersPanel() {
  const run = useMutation({
    mutationFn: () => apiFetch<ReminderRun>('/api/jobs/reminders/run', { method: 'POST' }),
  });
  const result = run.data;

  return (
    <div className="space-y-3">
      <p>
        Reminders and overdue notices are sent automatically every hour. Run the same job now to send any that are due.
        Each party is emailed at most once per reminder interval, so running it again does nothing new.
      </p>
      <ErrorList messages={messagesOf(run.error)} />
      {result && (
        <div role="status" className="rounded-md bg-surface-muted p-3 text-sm text-ink">
          <p className="font-medium">Reminder job finished.</p>
          <ul className="mt-1 list-disc pl-5">
            <li>{plural(result.remindersSent, 'reminder')} sent</li>
            <li>{plural(result.overdueNoticesSent, 'overdue notice')} sent</li>
            <li>{result.failed} failed</li>
          </ul>
        </div>
      )}
      <button type="button" disabled={run.isPending} onClick={() => run.mutate()} className={secondaryButtonClass}>
        {run.isPending ? 'Running...' : 'Run reminders now'}
      </button>
    </div>
  );
}

interface DemoReset {
  requests: number;
  contacts: number;
}

function ResetDemoPanel() {
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const reset = useMutation({
    mutationFn: () => apiFetch<DemoReset>('/api/demo/reset', { method: 'POST' }),
    onSuccess: () => {
      setConfirming(false);
      void queryClient.invalidateQueries();
    },
  });

  return (
    <div className="space-y-3">
      <p>
        Deletes all requests, contacts, uploaded files and stored emails, then creates the sample data again. The audit log
        is kept and the reset is recorded in it.
      </p>
      <ErrorList messages={messagesOf(reset.error)} />
      {reset.isSuccess && (
        <p role="status" className="text-sm text-success-700">
          Demo data reset. {plural(reset.data.requests, 'sample request')} created.
        </p>
      )}
      {confirming ? (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm text-ink">Delete all current request data and start again?</span>
          <button type="button" disabled={reset.isPending} onClick={() => reset.mutate()} className={primaryButtonClass}>
            {reset.isPending ? 'Resetting...' : 'Yes, reset'}
          </button>
          <button
            type="button"
            disabled={reset.isPending}
            onClick={() => setConfirming(false)}
            className={secondaryButtonClass}
          >
            Cancel
          </button>
        </div>
      ) : (
        <button type="button" onClick={() => setConfirming(true)} className={secondaryButtonClass}>
          Reset demo data
        </button>
      )}
    </div>
  );
}

interface RetentionPreview {
  retentionDays: number;
  cutoff: string;
  requestCount: number;
  fileCount: number;
  contactCount: number;
  requests: PagedResponse<{ id: string; reference: string; name: string; completedAt: string; fileCount: number }>;
}

interface RetentionRun {
  requestsDeleted: number;
  filesDeleted: number;
  contactsPseudonymised: number;
}

function RetentionPanel() {
  const queryClient = useQueryClient();
  const paging = usePaging();
  const [previewing, setPreviewing] = useState(false);
  const [confirming, setConfirming] = useState(false);

  // Each preview is recorded in the audit log, so it is only fetched on request and never refetched in the background.
  const preview = useQuery({
    queryKey: ['retention-preview', paging.page, paging.pageSize],
    queryFn: () => apiFetch<RetentionPreview>(`/api/gdpr/retention/preview?${paging.params()}`),
    enabled: previewing,
    staleTime: Infinity,
    gcTime: 0,
  });
  useClampPage(paging, preview.data?.requests.total);

  const run = useMutation({
    mutationFn: () => apiFetch<RetentionRun>('/api/gdpr/retention/run', { method: 'POST' }),
    onSuccess: () => {
      setConfirming(false);
      setPreviewing(false);
      queryClient.removeQueries({ queryKey: ['retention-preview'] });
      void queryClient.invalidateQueries({ queryKey: ['requests'] });
      void queryClient.invalidateQueries({ queryKey: ['contacts'] });
    },
  });

  const data = preview.data;

  return (
    <div className="space-y-3">
      <p>
        Requests completed more than the retention period ago can be purged: their files and data are deleted and contacts
        left without a request are pseudonymised. Preview first; the preview changes nothing. Both are recorded in the
        audit log.
      </p>
      <ErrorList messages={[...messagesOf(preview.error), ...messagesOf(run.error)]} />
      {run.isSuccess && (
        <p role="status" className="text-sm text-success-700">
          Purge finished. {plural(run.data.requestsDeleted, 'request')} and {plural(run.data.filesDeleted, 'file')} deleted,{' '}
          {plural(run.data.contactsPseudonymised, 'contact')} pseudonymised.
        </p>
      )}
      {data && (
        <div className="space-y-3 text-sm text-ink">
          <p>
            Cut-off {new Date(data.cutoff).toLocaleDateString()} ({data.retentionDays} days): {plural(data.requestCount, 'request')},{' '}
            {plural(data.fileCount, 'file')} and {plural(data.contactCount, 'contact')} would be purged.
          </p>
          {data.requests.items.length > 0 && (
            <ul className="divide-y divide-surface-border rounded-md border border-surface-border">
              {data.requests.items.map((request) => (
                <li key={request.id} className="flex flex-wrap justify-between gap-2 p-2">
                  <span>
                    {request.reference} - {request.name}
                  </span>
                  <span className="text-ink-muted">
                    completed {new Date(request.completedAt).toLocaleDateString()}, {plural(request.fileCount, 'file')}
                  </span>
                </li>
              ))}
            </ul>
          )}
          <Pagination
            page={paging.page}
            pageSize={paging.pageSize}
            total={data.requests.total}
            onPageChange={paging.setPage}
            onPageSizeChange={paging.setPageSize}
          />
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          disabled={preview.isFetching}
          onClick={() => {
            setConfirming(false);
            if (previewing) void preview.refetch();
            else setPreviewing(true);
          }}
          className={secondaryButtonClass}
        >
          {preview.isFetching ? 'Loading preview...' : 'Preview purge'}
        </button>
        {data && data.requestCount > 0 && !confirming && (
          <button type="button" onClick={() => setConfirming(true)} className={secondaryButtonClass}>
            Run purge...
          </button>
        )}
        {confirming && (
          <>
            <span className="text-sm text-ink">Permanently delete these requests and their files?</span>
            <button type="button" disabled={run.isPending} onClick={() => run.mutate()} className={primaryButtonClass}>
              {run.isPending ? 'Purging...' : 'Yes, purge'}
            </button>
            <button type="button" disabled={run.isPending} onClick={() => setConfirming(false)} className={secondaryButtonClass}>
              Cancel
            </button>
          </>
        )}
      </div>
    </div>
  );
}

export function SettingsPage() {
  const settings = useQuery({ queryKey: ['settings'], queryFn: () => apiFetch<Settings>('/api/settings') });
  const demo = useDemoConfig();

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">Settings</h1>
      {settings.isPending && <p className="text-sm text-ink-muted">Loading settings...</p>}
      {settings.isError && <ErrorList messages={messagesOf(settings.error)} />}
      {settings.data && (
        <Card title="Reminders and retention">
          <SettingsForm settings={settings.data} />
        </Card>
      )}
      <Card title="Reminder job">
        <RunRemindersPanel />
      </Card>
      <Card title="Retention purge">
        <RetentionPanel />
      </Card>
      {demo.enabled && (
        <Card title="Demo data">
          <ResetDemoPanel />
        </Card>
      )}
    </div>
  );
}
