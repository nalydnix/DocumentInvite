import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { hasAnyRole, useAuth } from '../auth/AuthContext';
import { Pagination } from '../components/Pagination';
import { ErrorList, primaryButtonClass, secondaryButtonClass } from '../components/Field';
import { ApiError, apiFetch } from '../lib/api';
import { useClampPage, usePaging, type PagedResponse } from '../lib/paging';

export interface TemplateSummary {
  id: string;
  name: string;
  description: string;
  isSeeded: boolean;
  isArchived: boolean;
  itemCount: number;
}

function messagesOf(error: unknown): string[] {
  return error instanceof ApiError ? error.messages : error ? ['Something went wrong. Please try again.'] : [];
}

function TemplateRow({ template, canEdit }: { template: TemplateSummary; canEdit: boolean }) {
  const queryClient = useQueryClient();
  const toggle = useMutation({
    mutationFn: () =>
      apiFetch<TemplateSummary>(`/api/templates/${template.id}/${template.isArchived ? 'restore' : 'archive'}`, {
        method: 'POST',
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['templates'] }),
  });

  return (
    <li className="space-y-2 rounded-card border border-surface-border bg-surface p-4 shadow-card">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <Link to={`/templates/${template.id}`} className="font-medium text-brand-700 hover:underline">
            {template.name}
          </Link>
          <p className="text-sm text-ink-muted">
            {template.itemCount} {template.itemCount === 1 ? 'item' : 'items'}
          </p>
        </div>
        <div className="flex gap-2">
          {template.isArchived && (
            <span className="rounded-full bg-surface-muted px-2 py-0.5 text-xs font-medium text-ink-muted">
              Archived
            </span>
          )}
        </div>
      </div>
      {template.description && <p className="text-sm text-ink-muted">{template.description}</p>}
      {canEdit && (
        <button
          type="button"
          disabled={toggle.isPending}
          onClick={() => toggle.mutate()}
          className={secondaryButtonClass}
          aria-label={`${template.isArchived ? 'Restore' : 'Archive'} ${template.name}`}
        >
          {template.isArchived ? 'Restore' : 'Archive'}
        </button>
      )}
      <ErrorList messages={messagesOf(toggle.error)} />
    </li>
  );
}

export function TemplatesPage() {
  const { state } = useAuth();
  const canEdit = state.status === 'authenticated' && hasAnyRole(state.user, ['Admin', 'LoanOfficer']);
  const paging = usePaging();
  const templates = useQuery({
    queryKey: ['templates', 'list', paging.page, paging.pageSize],
    queryFn: () => apiFetch<PagedResponse<TemplateSummary>>(`/api/templates?${paging.params()}&includeArchived=true`),
    placeholderData: keepPreviousData,
  });
  useClampPage(paging, templates.data?.total);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold text-ink">Checklist templates</h1>
        {canEdit && (
          <Link to="/templates/new" className={primaryButtonClass}>
            New template
          </Link>
        )}
      </div>
      {templates.isPending && <p className="text-sm text-ink-muted">Loading templates...</p>}
      {templates.isError && <ErrorList messages={messagesOf(templates.error)} />}
      {templates.data && templates.data.total === 0 && (
        <p className="text-sm text-ink-muted">No templates yet.</p>
      )}
      {templates.data && templates.data.items.length > 0 && (
        <ul className="space-y-3" aria-label="Templates">
          {templates.data.items.map((template) => (
            <TemplateRow key={`${template.id}:${template.isArchived}`} template={template} canEdit={canEdit} />
          ))}
        </ul>
      )}
      {templates.data && (
        <Pagination
          page={paging.page}
          pageSize={paging.pageSize}
          total={templates.data.total}
          onPageChange={paging.setPage}
          onPageSizeChange={paging.setPageSize}
        />
      )}
    </div>
  );
}
