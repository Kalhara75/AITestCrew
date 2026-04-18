import { useState } from 'react';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';
import { ConfirmDialog } from './ConfirmDialog';

interface Props {
  testSetId: string;
  objectiveId: string;
  parentObjective: string;
  source: string;
  moduleId?: string;
  apiStackKey?: string | null;
  apiModule?: string | null;
  environmentKey?: string | null;
  disabled?: boolean;
  hasDeliveryVerifications?: boolean;
}

export function TriggerObjectiveRunButton({ testSetId, objectiveId, parentObjective, source, moduleId, apiStackKey, apiModule, environmentKey, disabled, hasDeliveryVerifications }: Props) {
  const { individualRun, individualRunStatus, setIndividualRun } = useActiveRun();
  const [error, setError] = useState<string | null>(null);
  const [showRebaselineConfirm, setShowRebaselineConfirm] = useState(false);

  // This button is "active" if the global individual run targets this specific objective
  const isActive = individualRun?.testSetId === testSetId && individualRun?.objectiveId === objectiveId;
  // Any individual run is in progress (disable other run buttons)
  const anyRunning = !!individualRun;

  const isRecorded = source === 'Recorded';

  const fireRun = async (mode: 'Reuse' | 'Rebaseline' | 'VerifyOnly') => {
    setError(null);
    try {
      const res = await triggerRun({
        mode,
        testSetId,
        moduleId,
        objectiveId,
        objective: mode === 'Rebaseline' ? parentObjective : undefined,
        apiStackKey: apiStackKey ?? undefined,
        apiModule: apiModule ?? undefined,
        environmentKey: environmentKey ?? undefined,
        verificationWaitOverride: mode === 'VerifyOnly' ? 0 : undefined,
      });
      setIndividualRun({ runId: res.runId, testSetId, moduleId, objectiveId });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to trigger run');
    }
  };

  const handleRun = (e: React.MouseEvent) => {
    e.stopPropagation();
    fireRun('Reuse');
  };

  const handleVerifyOnly = (e: React.MouseEvent) => {
    e.stopPropagation();
    fireRun('VerifyOnly');
  };

  const handleRebaseline = (e: React.MouseEvent) => {
    e.stopPropagation();
    setShowRebaselineConfirm(true);
  };

  const confirmRebaseline = () => {
    setShowRebaselineConfirm(false);
    fireRun('Rebaseline');
  };

  if (isActive) {
    const s = individualRunStatus?.status;
    const queued = s === 'Queued' || s === 'Claimed';
    return (
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }} title={queued ? 'Waiting for agent' : 'Running'}>
        <div style={{
          width: 12, height: 12,
          border: `2px solid ${queued ? '#fde68a' : '#bfdbfe'}`,
          borderTop: `2px solid ${queued ? '#b45309' : '#2563eb'}`,
          borderRadius: '50%',
          animation: 'spin 0.8s linear infinite',
        }} />
        {queued && <span style={{ fontSize: 10, color: '#78350f' }}>queued</span>}
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>
    );
  }

  return (
    <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      <button
        onClick={handleRun}
        disabled={disabled || anyRunning}
        style={{
          background: 'none',
          color: disabled || anyRunning ? '#94a3b8' : '#2563eb',
          border: `1px solid ${disabled || anyRunning ? '#e2e8f0' : '#bfdbfe'}`,
          padding: '1px 8px',
          borderRadius: 4,
          fontSize: 11,
          fontWeight: 600,
          cursor: disabled || anyRunning ? 'not-allowed' : 'pointer',
          lineHeight: '18px',
        }}
        title="Run this test case"
      >
        Run
      </button>
      {hasDeliveryVerifications && (
        <button
          onClick={handleVerifyOnly}
          disabled={disabled || anyRunning}
          style={{
            background: 'none',
            color: disabled || anyRunning ? '#94a3b8' : '#0d9488',
            border: `1px solid ${disabled || anyRunning ? '#e2e8f0' : '#ccfbf1'}`,
            padding: '1px 8px',
            borderRadius: 4,
            fontSize: 11,
            fontWeight: 600,
            cursor: disabled || anyRunning ? 'not-allowed' : 'pointer',
            lineHeight: '18px',
          }}
          title="Re-run post-delivery UI verifications only (skip delivery)"
        >
          Verify
        </button>
      )}
      {!isRecorded && (
        <button
          onClick={handleRebaseline}
          disabled={disabled || anyRunning}
          style={{
            background: 'none',
            color: disabled || anyRunning ? '#94a3b8' : '#d97706',
            border: `1px solid ${disabled || anyRunning ? '#e2e8f0' : '#fde68a'}`,
            padding: '1px 8px',
            borderRadius: 4,
            fontSize: 11,
            fontWeight: 600,
            cursor: disabled || anyRunning ? 'not-allowed' : 'pointer',
            lineHeight: '18px',
          }}
          title="Regenerate this objective via AI"
        >
          Rebaseline
        </button>
      )}
      {error && (
        <span style={{ color: '#dc2626', fontSize: 10 }} title={error}>!</span>
      )}
      <ConfirmDialog
        open={showRebaselineConfirm}
        title="Rebaseline Objective"
        message="This will regenerate all test cases for this objective using the AI engine. Existing test cases will be replaced. Continue?"
        confirmLabel="Rebaseline"
        confirmDestructive
        onConfirm={confirmRebaseline}
        onCancel={() => setShowRebaselineConfirm(false)}
      />
    </div>
  );
}
