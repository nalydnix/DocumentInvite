import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card } from '../components/Card';
import { ErrorList, Field, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
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
          <p role="status" className="text-sm text-green-700">
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
  loans: number;
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
        Deletes all loans, contacts, uploaded files and stored emails, then creates the sample data again. The audit log
        is kept and the reset is recorded in it.
      </p>
      <ErrorList messages={messagesOf(reset.error)} />
      {reset.isSuccess && (
        <p role="status" className="text-sm text-green-700">
          Demo data reset. {plural(reset.data.loans, 'sample loan')} created.
        </p>
      )}
      {confirming ? (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm text-ink">Delete all current loan data and start again?</span>
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
      {demo.enabled && (
        <Card title="Demo data">
          <ResetDemoPanel />
        </Card>
      )}
    </div>
  );
}
