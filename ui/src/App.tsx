import { Routes, Route } from 'react-router-dom';
import { Layout } from './components/Layout';
import { DashboardPage } from './pages/DashboardPage';
import { TestSetDetailPage } from './pages/TestSetDetailPage';
import { ExecutionDetailPage } from './pages/ExecutionDetailPage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/testsets/:id" element={<TestSetDetailPage />} />
        <Route path="/testsets/:id/runs/:runId" element={<ExecutionDetailPage />} />
      </Route>
    </Routes>
  );
}
