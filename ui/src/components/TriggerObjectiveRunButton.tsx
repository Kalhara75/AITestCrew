import { useState } from 'react';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';
import { ConfirmDialog } from './ConfirmDialog';
import { RunningIndicator } from './execution/RunningIndicator';
import { AgentPicker } from './AgentPicker';

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
  hasPostSteps?: boolean;
}

export function TriggerObjectiveRunButton({ testSetId, objectiveId, parentObjective, source, moduleId, apiStackKey, apiModule, environmentKey, disabled, hasPostSteps }: Props) {
  const { individualRun, individualRunStatus, setIndividualRun } = useActiveRun();
  const [error, setError] = useState<string | null>(null);
  const [showRebaselineConfirm, setShowRebaselineConfirm] = useState(false);
  const [preferredAgentId, setPreferredAgentId] = useState<string | null>(null);

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
        preferredAgentId: mode === 'Reuse' ? (preferredAgentId ?? undefined) : undefined,
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
    const awaiting = s === 'AwaitingVerification';
    const title = awaiting ? 'Awaiting deferred verification' : queued ? 'Waiting for agent' : 'Running';

    // Awaiting = scheduled, not running. Replace the spinner with a quiet
    // cyan pill so the user sees that nothing is actively executing.
    if (awaiting) {
      return (
        <span
          title={title}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 4,
            background: '#cffafe', color: '#0e7490',
            border: '1px solid #a5f3fc', borderRadius: 10,
            padding: '2px 8px', fontSize: 10, fontWeight: 600,
          }}>
          {'⏳'} awaiting
        </span>
      );
    }

    const label = queued ? 'queued' : null;
    return (
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }} title={title}>
        <RunningIndicator state={queued ? 'queued' : 'running'} size="sm" />
        {label && <span style={{ fontSize: 10, color: '#78350f' }}>{label}</span>}
      </div>
    );
  }

  return (
    <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4, flexWrap: 'wrap' }}>
      <AgentPicker value={preferredAgentId} onChange={setPreferredAgentId} disabled={disabled || anyRunning} />
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
      {hasPostSteps && (
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
          title="Re-run post-verification steps only (skip the parent test step)"
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
