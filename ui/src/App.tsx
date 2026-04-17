import { Routes, Route } from 'react-router-dom';
import { Layout } from './components/Layout';
import { ModuleListPage } from './pages/ModuleListPage';
import { ModuleDetailPage } from './pages/ModuleDetailPage';
import { TestSetDetailPage } from './pages/TestSetDetailPage';
import { ExecutionDetailPage } from './pages/ExecutionDetailPage';
import { LoginPage } from './pages/LoginPage';
import { useAuth } from './contexts/AuthContext';

export default function App() {
  const { user, authRequired, isLoading } = useAuth();

  if (isLoading) return null;

  // Show login when the server requires auth and we don't have a validated user
  if (authRequired && !user) return <LoginPage />;

  return (
    <Routes>
      <Route element={<Layout />}>
        {/* Module-based routes */}
        <Route path="/" element={<ModuleListPage />} />
        <Route path="/modules/:moduleId" element={<ModuleDetailPage />} />
        <Route path="/modules/:moduleId/testsets/:id" element={<TestSetDetailPage />} />
        <Route path="/modules/:moduleId/testsets/:id/runs/:runId" element={<ExecutionDetailPage />} />

        {/* Legacy routes (for backward-compat bookmarks) */}
        <Route path="/testsets/:id" element={<TestSetDetailPage />} />
        <Route path="/testsets/:id/runs/:runId" element={<ExecutionDetailPage />} />

        {/* Login route for manual navigation */}
        <Route path="/login" element={<LoginPage />} />
      </Route>
    </Routes>
  );
}
