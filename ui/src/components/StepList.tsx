import { useState } from 'react';
import type { TaskResult, StepResult } from '../types';
import { StatusBadge } from './StatusBadge';

export function StepList({ taskResults }: { taskResults: TaskResult[] }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {taskResults.map(task => (
        <TaskSection key={task.taskId} task={task} />
      ))}
    </div>
  );
}

function TaskSection({ task }: { task: TaskResult }) {
  const [expanded, setExpanded] = useState(true);

  return (
    <div style={{ background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0', overflow: 'hidden' }}>
      <div
        onClick={() => setExpanded(!expanded)}
        style={{
          padding: '14px 20px',
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          cursor: 'pointer',
          background: '#f8fafc',
          borderBottom: expanded ? '1px solid #e2e8f0' : 'none',
          transition: 'background 0.1s',
        }}
        onMouseEnter={e => (e.currentTarget.style.background = '#f1f5f9')}
        onMouseLeave={e => (e.currentTarget.style.background = '#f8fafc')}
      >
        <span style={{
          fontSize: 10,
          color: '#94a3b8',
          transition: 'transform 0.15s',
          transform: expanded ? 'rotate(90deg)' : 'rotate(0deg)',
          display: 'inline-block',
        }}>
          {'\u25B6'}
        </span>
        <StatusBadge status={task.status} />
        <span style={{ fontWeight: 600, fontSize: 14, color: '#1e293b', flex: 1 }}>
          {task.agentName}
          <span style={{ fontWeight: 400, color: '#64748b', marginLeft: 8 }}>
            {task.summary.length > 80 ? task.summary.slice(0, 80) + '...' : task.summary}
          </span>
        </span>
        <span style={{
          fontSize: 13,
          color: '#64748b',
          fontWeight: 500,
          background: '#f1f5f9',
          padding: '2px 10px',
          borderRadius: 6,
        }}>
          {task.passedSteps}/{task.totalSteps} steps
        </span>
      </div>
      {expanded && (
        <div>
          {task.steps.map((step, i) => (
            <StepRow key={i} step={step} isLast={i === task.steps.length - 1} />
          ))}
        </div>
      )}
    </div>
  );
}

function StepRow({ step, isLast }: { step: StepResult; isLast: boolean }) {
  const [showDetail, setShowDetail] = useState(false);
  const statusIcon = step.status === 'Passed' ? '\u2705' : step.status === 'Failed' ? '\u274C' : '\u26A0\uFE0F';

  return (
    <div style={{ borderBottom: isLast ? 'none' : '1px solid #f1f5f9' }}>
      <div
        onClick={() => step.detail && setShowDetail(!showDetail)}
        style={{
          padding: '10px 20px',
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          cursor: step.detail ? 'pointer' : 'default',
          fontSize: 13,
          transition: 'background 0.1s',
        }}
        onMouseEnter={e => { if (step.detail) e.currentTarget.style.background = '#fafafa'; }}
        onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}
      >
        <span style={{ width: 20, textAlign: 'center' }}>{statusIcon}</span>
        <span style={{
          fontFamily: 'ui-monospace, Consolas, monospace',
          fontWeight: 600,
          color: '#334155',
          minWidth: 160,
          fontSize: 12,
        }}>
          {step.action}
        </span>
        <span style={{ color: '#64748b', flex: 1 }}>{step.summary}</span>
        {step.detail && (
          <span style={{ color: '#94a3b8', fontSize: 11 }}>{showDetail ? 'Hide' : 'Detail'}</span>
        )}
      </div>
      {showDetail && step.detail && (
        <pre style={{
          margin: 0,
          padding: '12px 20px 16px 50px',
          fontSize: 12,
          color: '#475569',
          background: '#f8fafc',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
          maxHeight: 300,
          overflow: 'auto',
          borderTop: '1px solid #f1f5f9',
          fontFamily: 'ui-monospace, Consolas, monospace',
        }}>
          {step.detail}
        </pre>
      )}
    </div>
  );
}
