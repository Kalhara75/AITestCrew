import { Routes, Route } from 'react-router-dom';
import { Layout } from './components/Layout';
import { ModuleListPage } from './pages/ModuleListPage';
import { ModuleDetailPage } from './pages/ModuleDetailPage';
import { TestSetDetailPage } from './pages/TestSetDetailPage';
import { ExecutionDetailPage } from './pages/ExecutionDetailPage';

export default function App() {
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
      </Route>
    </Routes>
  );
}
