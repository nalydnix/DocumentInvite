import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { jsonResponse, meBody, mockApi, renderApp, resetSession, tokens } from '../test-utils';
import { TemplateEditorPage, type TemplateDetail } from './TemplateEditorPage';
import { TemplatesPage, type TemplateSummary } from './TemplatesPage';

afterEach(resetSession);

const template: TemplateDetail = {
  id: 't1',
  name: 'Checklist',
  description: 'Desc',
  isSeeded: false,
  isArchived: false,
  items: [
    { id: 'i1', name: 'Tax returns', kind: 'FileUpload', instructions: '', allowMultiple: true, fieldType: null, isRequired: true, sortOrder: 0 },
    { id: 'i2', name: 'Years in business', kind: 'FormField', instructions: '', allowMultiple: false, fieldType: 'Number', isRequired: false, sortOrder: 1 },
  ],
};

function signedIn(role: 'Admin' | 'LoanOfficer' | 'Viewer' = 'LoanOfficer') {
  sessionStorage.setItem('di.refreshToken', 'refresh-1');
  return {
    'POST /api/auth/refresh': () => jsonResponse(200, tokens),
    'GET /api/me': () => jsonResponse(200, meBody([role])),
  };
}

function renderEditor(route: string) {
  return renderApp(
    <Routes>
      <Route path="/templates" element={<p>Template list</p>} />
      <Route path="/templates/new" element={<TemplateEditorPage />} />
      <Route path="/templates/:id" element={<TemplateEditorPage />} />
    </Routes>,
    route,
  );
}

describe('TemplateEditorPage', () => {
  it('creates a template with a file upload and a form field', async () => {
    const { calls } = mockApi({ ...signedIn(), 'POST /api/templates': () => jsonResponse(201, template) });
    renderEditor('/templates/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Template name'), 'New checklist');
    await user.click(screen.getByRole('button', { name: 'Add file upload' }));
    await user.type(screen.getByLabelText('Name of item 1'), 'Bank statements');
    await user.click(screen.getByLabelText('Item 1 allows multiple files'));
    await user.click(screen.getByRole('button', { name: 'Add form field' }));
    await user.type(screen.getByLabelText('Name of item 2'), 'Revenue');
    await user.selectOptions(screen.getByLabelText('Field type of item 2'), 'Number');
    await user.click(screen.getByRole('button', { name: 'Save template' }));

    const post = calls.find((c) => c.key === 'POST /api/templates');
    expect(JSON.parse(post!.init!.body as string)).toEqual({
      name: 'New checklist',
      description: '',
      items: [
        { id: null, name: 'Bank statements', kind: 'FileUpload', instructions: '', allowMultiple: true, fieldType: null, isRequired: true },
        { id: null, name: 'Revenue', kind: 'FormField', instructions: '', allowMultiple: false, fieldType: 'Number', isRequired: true },
      ],
    });
    expect(await screen.findByText('Template list')).toBeInTheDocument();
  });

  it('reorders items and keeps their ids when saving an edit', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/templates/t1': () => jsonResponse(200, template),
      'PUT /api/templates/t1': () => jsonResponse(200, template),
    });
    renderEditor('/templates/t1');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Move item 2 up' }));
    expect(screen.getByLabelText('Name of item 1')).toHaveValue('Years in business');
    await user.click(screen.getByRole('button', { name: 'Remove item 2' }));
    await user.click(screen.getByRole('button', { name: 'Save template' }));

    const put = calls.find((c) => c.key === 'PUT /api/templates/t1');
    const body = JSON.parse(put!.init!.body as string);
    expect(body.items).toHaveLength(1);
    expect(body.items[0]).toMatchObject({ id: 'i2', name: 'Years in business', fieldType: 'Number' });
  });

  it('shows API validation errors', async () => {
    mockApi({
      ...signedIn(),
      'POST /api/templates': () =>
        jsonResponse(400, { title: 'Validation', errors: { 'items[0].fieldType': ['A form field requires a field type.'] } }),
    });
    renderEditor('/templates/new');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Template name'), 'X');
    await user.click(screen.getByRole('button', { name: 'Save template' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('requires a field type');
  });

  it('is read-only for viewers', async () => {
    mockApi({ ...signedIn('Viewer'), 'GET /api/templates/t1': () => jsonResponse(200, template) });
    renderEditor('/templates/t1');

    expect(await screen.findByLabelText('Template name')).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save template' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add file upload' })).not.toBeInTheDocument();
  });
});

describe('TemplatesPage', () => {
  const summaries: TemplateSummary[] = [
    { id: 't1', name: 'SBA checklist', description: 'Buy a business', isSeeded: true, isArchived: false, itemCount: 14 },
    { id: 't2', name: 'Old checklist', description: '', isSeeded: false, isArchived: true, itemCount: 1 },
  ];

  it('lists templates and archives one', async () => {
    const { calls } = mockApi({
      ...signedIn(),
      'GET /api/templates?includeArchived=true': () => jsonResponse(200, summaries),
      'POST /api/templates/t1/archive': () => jsonResponse(200, { ...summaries[0], isArchived: true }),
    });
    renderApp(<TemplatesPage />);

    expect(await screen.findByText('SBA checklist')).toBeInTheDocument();
    expect(screen.getByText('14 items')).toBeInTheDocument();
    expect(screen.getByText('Archived')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Archive SBA checklist' }));

    expect(calls.some((c) => c.key === 'POST /api/templates/t1/archive')).toBe(true);
    expect(screen.getByRole('button', { name: 'Restore Old checklist' })).toBeInTheDocument();
  });

  it('hides editing actions from viewers', async () => {
    mockApi({
      ...signedIn('Viewer'),
      'GET /api/templates?includeArchived=true': () => jsonResponse(200, summaries),
    });
    renderApp(<TemplatesPage />);

    expect(await screen.findByText('SBA checklist')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'New template' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Archive/ })).not.toBeInTheDocument();
  });
});
