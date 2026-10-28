import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Card } from '../components/Card';
import { ErrorList, Field, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { LOAN_WRITER_ROLES, type Loan } from './loans';

const LOAN_TYPE_SUGGESTIONS = ['SBA 7(a)', 'Commercial real estate', 'Equipment finance', 'Line of credit'];

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function LoanForm() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [values, setValues] = useState({ reference: '', name: '', amount: '', loanType: '' });
  const set = (key: keyof typeof values, value: string) => setValues((current) => ({ ...current, [key]: value }));

  const save = useMutation({
    mutationFn: () =>
      apiFetch<Loan>('/api/loans', {
        method: 'POST',
        body: { ...values, amount: values.amount === '' ? null : Number(values.amount) },
      }),
    onSuccess: async (loan) => {
      await queryClient.invalidateQueries({ queryKey: ['loans'] });
      navigate(`/loans/${loan.id}`);
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
        label="Loan name"
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
        label="Loan type"
        required
        maxLength={100}
        list="loan-type-suggestions"
        value={values.loanType}
        onChange={(e) => set('loanType', e.target.value)}
      />
      <datalist id="loan-type-suggestions">
        {LOAN_TYPE_SUGGESTIONS.map((type) => (
          <option key={type} value={type} />
        ))}
      </datalist>
      <div className="space-y-3 sm:col-span-2">
        <ErrorList messages={messagesOf(save.error)} />
        <div className="flex flex-wrap gap-3">
          <button type="submit" disabled={save.isPending} className={primaryButtonClass}>
            Create loan
          </button>
          <Link to="/loans" className={secondaryButtonClass}>
            Cancel
          </Link>
        </div>
      </div>
    </form>
  );
}

export function LoanFormPage() {
  const { state } = useAuth();
  const canWrite = state.status === 'authenticated' && hasAnyRole(state.user, LOAN_WRITER_ROLES);
  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-ink">New loan</h1>
      {canWrite ? (
        <Card title="Loan details">
          <LoanForm />
        </Card>
      ) : (
        <Card title="Not authorised">Your role cannot create loans.</Card>
      )}
    </div>
  );
}
