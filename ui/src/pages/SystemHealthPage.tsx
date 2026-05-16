import { AgentsPanel } from '../components/AgentsPanel';
import { DataPacksPanel } from '../components/DataPacksPanel';
import { BackupHealthPanel } from '../components/BackupHealthPanel';
import { AuthHealthPanel } from '../components/AuthHealthPanel';

export function SystemHealthPage() {
  return (
    <div>
      <div style={{ marginBottom: 24 }}>
        <h1 style={{ margin: '0 0 4px', fontSize: 24, fontWeight: 700, color: '#0f172a' }}>
          System Health
        </h1>
        <p style={{ margin: 0, fontSize: 14, color: '#64748b' }}>
          Agents, data packs, backups, and the full auth-state picture across every environment.
        </p>
      </div>
      <AgentsPanel />
      <DataPacksPanel />
      <BackupHealthPanel />
      <AuthHealthPanel />
    </div>
  );
}
