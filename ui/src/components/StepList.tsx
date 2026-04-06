import { useState } from 'react';
import type { ObjectiveResult, StepResult } from '../types';
import { StatusBadge } from './StatusBadge';

export function StepList({ objectiveResults }: { objectiveResults: ObjectiveResult[] }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {objectiveResults.map(objective => (
        <ObjectiveSection key={objective.objectiveId} objective={objective} />
      ))}
    </div>
  );
}

function ObjectiveSection({ objective }: { objective: ObjectiveResult }) {
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
        <StatusBadge status={objective.status} />
        <span style={{ fontWeight: 600, fontSize: 14, color: '#1e293b', flex: 1 }}>
          {objective.agentName}
          <span style={{ fontWeight: 400, color: '#64748b', marginLeft: 8 }}>
            {objective.summary.length > 80 ? objective.summary.slice(0, 80) + '...' : objective.summary}
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
          {objective.passedSteps}/{objective.totalSteps} steps
        </span>
      </div>
      {expanded && (
        <div>
          {objective.steps.map((step, i) => (
            <StepRow key={i} step={step} isLast={i === objective.steps.length - 1} />
          ))}
        </div>
      )}
    </div>
  );
}

/** Extract screenshot filename from detail text like "...| Screenshot: filename.png" */
function parseScreenshot(detail: string | null): { text: string; screenshotFile: string | null } {
  if (!detail) return { text: '', screenshotFile: null };
  const match = detail.match(/\| Screenshot: (.+\.png)$/);
  if (!match) return { text: detail, screenshotFile: null };
  return {
    text: detail.slice(0, match.index).trim(),
    screenshotFile: match[1],
  };
}

function StepRow({ step, isLast }: { step: StepResult; isLast: boolean }) {
  const [showDetail, setShowDetail] = useState(false);
  const statusIcon = step.status === 'Passed' ? '\u2705' : step.status === 'Failed' ? '\u274C' : '\u26A0\uFE0F';
  const { text: detailText, screenshotFile } = parseScreenshot(step.detail);
  const hasDetail = !!(step.detail);

  return (
    <div style={{ borderBottom: isLast ? 'none' : '1px solid #f1f5f9' }}>
      <div
        onClick={() => hasDetail && setShowDetail(!showDetail)}
        style={{
          padding: '10px 20px',
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          cursor: hasDetail ? 'pointer' : 'default',
          fontSize: 13,
          transition: 'background 0.1s',
        }}
        onMouseEnter={e => { if (hasDetail) e.currentTarget.style.background = '#fafafa'; }}
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
        {hasDetail && (
          <span style={{ color: '#94a3b8', fontSize: 11 }}>{showDetail ? 'Hide' : 'Detail'}</span>
        )}
      </div>
      {showDetail && hasDetail && (
        <div style={{
          padding: '12px 20px 16px 50px',
          background: '#f8fafc',
          borderTop: '1px solid #f1f5f9',
        }}>
          <pre style={{
            margin: 0,
            fontSize: 12,
            color: '#475569',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            fontFamily: 'ui-monospace, Consolas, monospace',
          }}>
            {detailText}
          </pre>
          {screenshotFile && (
            <div style={{ marginTop: 12 }}>
              <a
                href={`http://localhost:5050/screenshots/${encodeURIComponent(screenshotFile)}`}
                target="_blank"
                rel="noopener noreferrer"
                style={{ color: '#2563eb', fontSize: 12, fontWeight: 500 }}
              >
                View Screenshot
              </a>
              <img
                src={`http://localhost:5050/screenshots/${encodeURIComponent(screenshotFile)}`}
                alt="Failure screenshot"
                style={{
                  display: 'block',
                  marginTop: 8,
                  maxWidth: '100%',
                  maxHeight: 400,
                  borderRadius: 6,
                  border: '1px solid #e2e8f0',
                }}
                onError={e => { (e.target as HTMLImageElement).style.display = 'none'; }}
              />
            </div>
          )}
        </div>
      )}
    </div>
  );
}
