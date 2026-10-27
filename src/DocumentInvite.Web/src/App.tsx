import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { AppLayout } from './components/AppLayout';
import { ContactFormPage } from './pages/ContactFormPage';
import { ContactsPage } from './pages/ContactsPage';
import { HomePage } from './pages/HomePage';
import { LoginPage } from './pages/LoginPage';
import { TemplateEditorPage } from './pages/TemplateEditorPage';
import { TemplatesPage } from './pages/TemplatesPage';
import { OutboxPage } from './pages/OutboxPage';
import { UsersPage } from './pages/UsersPage';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route element={<ProtectedRoute />}>
              <Route element={<AppLayout />}>
                <Route index element={<HomePage />} />

                <Route element={<ProtectedRoute roles={['Admin', 'LoanOfficer']} />}>
                  <Route path="outbox" element={<OutboxPage />} />
                </Route>
                <Route element={<ProtectedRoute roles={['Admin', 'LoanOfficer', 'Viewer']} />}>
                  <Route path="templates" element={<TemplatesPage />} />
                  <Route path="templates/new" element={<TemplateEditorPage />} />
                  <Route path="templates/:id" element={<TemplateEditorPage />} />
                  <Route path="contacts" element={<ContactsPage />} />
                  <Route path="contacts/new" element={<ContactFormPage />} />
                  <Route path="contacts/:id" element={<ContactFormPage />} />

                </Route>
                <Route element={<ProtectedRoute roles={['Admin']} />}>
                  <Route path="users" element={<UsersPage />} />
                </Route>
              </Route>
            </Route>
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
