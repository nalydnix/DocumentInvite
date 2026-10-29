import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { HomeRedirect } from './auth/HomeRedirect';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { AppLayout } from './components/AppLayout';
import { AuditPage } from './pages/AuditPage';
import { ContactFormPage } from './pages/ContactFormPage';
import { ContactsPage } from './pages/ContactsPage';
import { RequestDetailPage } from './pages/RequestDetailPage';
import { RequestFormPage } from './pages/RequestFormPage';
import { RequestsPage } from './pages/RequestsPage';
import { LoginPage } from './pages/LoginPage';
import { SettingsPage } from './pages/SettingsPage';
import { TemplateEditorPage } from './pages/TemplateEditorPage';
import { TemplatesPage } from './pages/TemplatesPage';
import { OutboxPage } from './pages/OutboxPage';
import { PortalPage } from './portal/PortalPage';
import { UsersPage } from './pages/UsersPage';

/** Sends an old staff URL (before the rename) to its current path, keeping the id, query string and hash. */
export function LegacyRedirect({ to }: { to: string }) {
  const { id } = useParams();
  const { search, hash } = useLocation();
  return <Navigate to={`${to}${id ? `/${id}` : ''}${search}${hash}`} replace />;
}

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/p/:token" element={<PortalPage />} />
            <Route path="/loans" element={<LegacyRedirect to="/requests" />} />
            <Route path="/loans/:id" element={<LegacyRedirect to="/requests" />} />
            <Route element={<ProtectedRoute />}>
              <Route element={<AppLayout />}>
                <Route index element={<HomeRedirect />} />

                <Route element={<ProtectedRoute roles={['Admin', 'Requester']} />}>
                  <Route path="outbox" element={<OutboxPage />} />
                </Route>
                <Route element={<ProtectedRoute roles={['Admin', 'Requester']} />}>
                  <Route path="templates" element={<TemplatesPage />} />
                  <Route path="templates/new" element={<TemplateEditorPage />} />
                  <Route path="templates/:id" element={<TemplateEditorPage />} />
                  <Route path="requests" element={<RequestsPage />} />
                  <Route path="requests/new" element={<RequestFormPage />} />
                  <Route path="requests/:id" element={<RequestDetailPage />} />
                  <Route path="contacts" element={<ContactsPage />} />
                  <Route path="contacts/new" element={<ContactFormPage />} />
                  <Route path="contacts/:id" element={<ContactFormPage />} />

                </Route>
                <Route element={<ProtectedRoute roles={['Admin', 'Auditor']} />}>
                  <Route path="audit" element={<AuditPage />} />
                </Route>
                <Route element={<ProtectedRoute roles={['Admin']} />}>
                  <Route path="users" element={<UsersPage />} />
                  <Route path="settings" element={<SettingsPage />} />
                </Route>
              </Route>
            </Route>
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
