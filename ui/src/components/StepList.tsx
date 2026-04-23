import { useEffect, useState } from 'react';
import type { ObjectiveResult, StepResult } from '../types';
import { StatusBadge } from './StatusBadge';

/** Parse "pendingId=...\nremoteFile=...\nwaitSeconds=..." or "nextQueueEntryId=..." style detail
 *  into a lookup so we can surface useful info on AwaitingVerification rows. */
function parseAwaitingDetail(detail: string | null): Record<string, string> {
  const out: Record<string, string> = {};
  if (!detail) return out;
  for (const line of detail.split(/\r?\n/)) {
    const m = line.match(/^([A-Za-z0-9_]+)=(.*)$/);
    if (m) out[m[1]] = m[2];
  }
  return out;
}

/** Prefer the ISO UTC timestamp the agent stores in detail — deterministic across
 *  timezones. Fall back to parsing the HH:MM:SS from the summary as a last resort.
 *  The summary is intended for human display and should not be the source of truth
 *  for timing. */
function extractDueTime(summary: string, detail: string | null): Date | null {
  if (detail) {
    const m = detail.match(/firstDueAtUtc=([^\r\n]+)/);
    if (m) {
      const d = new Date(m[1]);
      if (!isNaN(d.getTime())) return d;
    }
  }
  // Fallback: parse HH:MM:SS from summary as browser-local time.
  const m = summary.match(/(?:attempt at|due|retrying at|due at)\s+(\d{2}:\d{2}:\d{2})/i);
  if (!m) return null;
  const [h, mi, s] = m[1].split(':').map(Number);
  const now = new Date();
  const target = new Date(now);
  target.setHours(h, mi, s, 0);
  // If the parsed time is earlier today than now, assume it's tomorrow (clock wrap).
  if (target.getTime() < now.getTime() - 60_000) target.setDate(target.getDate() + 1);
  return target;
}

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
  const isAwaiting = step.status === 'AwaitingVerification';
  const statusIcon = step.status === 'Passed' ? '\u2705'
    : step.status === 'Failed' ? '\u274C'
    : isAwaiting ? '\u23F3'
    : '\u26A0\uFE0F';
  const { text: detailText, screenshotFile } = parseScreenshot(step.detail);
  const hasDetail = !!(step.detail);
  const dueTime = isAwaiting ? extractDueTime(step.summary, step.detail) : null;

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
        <span style={{ color: isAwaiting ? '#0e7490' : '#64748b', flex: 1 }}>{step.summary}</span>
        {dueTime && <Countdown target={dueTime} />}
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

function Countdown({ target }: { target: Date }) {
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 1000);
    return () => clearInterval(id);
  }, []);
  // keep the tick reference to force re-renders
  void tick;

  const diffMs = target.getTime() - Date.now();
  if (diffMs <= 0) {
    return (
      <span style={{
        background: '#fef9c3',
        color: '#854d0e',
        padding: '2px 8px',
        borderRadius: 10,
        fontSize: 11,
        fontWeight: 600,
      }}>awaiting claim</span>
    );
  }
  const totalSeconds = Math.floor(diffMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  const label = minutes > 0 ? `in ${minutes}m ${seconds}s` : `in ${seconds}s`;
  return (
    <span style={{
      background: '#cffafe',
      color: '#0e7490',
      padding: '2px 8px',
      borderRadius: 10,
      fontSize: 11,
      fontWeight: 600,
      fontFamily: 'ui-monospace, Consolas, monospace',
    }}>{label}</span>
  );
}
