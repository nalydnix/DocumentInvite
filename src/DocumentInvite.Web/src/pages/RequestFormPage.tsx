import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { ErrorList, Field, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { REQUEST_WRITER_ROLES, type Request } from './requests';

const REQUEST_TYPE_SUGGESTIONS = ['SBA 7(a)', 'Commercial real estate', 'Equipment finance', 'Line of credit'];

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function RequestForm() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [values, setValues] = useState({ reference: '', name: '', amount: '', requestType: '' });
  const set = (key: keyof typeof values, value: string) => setValues((current) => ({ ...current, [key]: value }));

  const save = useMutation({
    mutationFn: () =>
      apiFetch<Request>('/api/requests', {
        method: 'POST',
        body: { ...values, amount: values.amount === '' ? null : Number(values.amount) },
      }),
    onSuccess: async (request) => {
      await queryClient.invalidateQueries({ queryKey: ['requests'] });
      navigate(`/requests/${request.id}`);
    },
  });

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  return (
    <form onSubmit={handleSubmit} className="grid gap-3 sm:grid-cols-2">
      <Field
        label="Reference"
        required
        maxLength={50}
        value={values.reference}
        onChange={(e) => set('reference', e.target.value)}
      />
      <Field
        label="Request name"
        required
        maxLength={200}
        value={values.name}
        onChange={(e) => set('name', e.target.value)}
      />
      <Field
        label="Amount (USD)"
        type="number"
        required
        min="0.01"
        step="0.01"
        value={values.amount}
        onChange={(e) => set('amount', e.target.value)}
      />
      <Field
        label="Request type"
        required
        maxLength={100}
        list="request-type-suggestions"
        value={values.requestType}
        onChange={(e) => set('requestType', e.target.value)}
      />
      <datalist id="request-type-suggestions">
        {REQUEST_TYPE_SUGGESTIONS.map((type) => (
          <option key={type} value={type} />
        ))}
      </datalist>
      <div className="space-y-3 sm:col-span-2">
        <ErrorList messages={messagesOf(save.error)} />
        <div className="flex flex-wrap gap-3">
          <button type="submit" disabled={save.isPending} className={primaryButtonClass}>
            Create request
          </button>
          <Link to="/requests" className={secondaryButtonClass}>
            Cancel
          </Link>
        </div>
      </div>
    </form>
  );
}

export function RequestFormPage() {
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, REQUEST_WRITER_ROLES);
  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">New request</h1>
      {canWrite ? (
        <Card title="Request details">
          <RequestForm />
        </Card>
      ) : (
        <Card title="Not authorised">Your role cannot create requests.</Card>
      )}
    </div>
  );
}
