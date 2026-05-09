import { useState } from 'react';
import type { ObjectiveResult, StepResult } from '../types';
import { StatusBadge } from './execution/StatusBadge';
import { DeferredCountdownChip } from './execution/DeferredCountdownChip';

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
          {'▶'}
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
  const { text: detailText, screenshotFile } = parseScreenshot(step.detail);
  const dbDiagnostics = extractDbDiagnostics(step);
  const eventAssertDiagnostics = extractEventAssertDiagnostics(step);
  const hasDetail = !!(step.detail) || dbDiagnostics !== null || eventAssertDiagnostics !== null;
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
        {/* StatusBadge replaces hardcoded emoji status icons */}
        <StatusBadge status={step.status} size="sm" />
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
        {dueTime && <DeferredCountdownChip target={dueTime} />}
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
          {detailText && (
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
          )}
          {dbDiagnostics && <DbDiagnosticsTable diag={dbDiagnostics} />}
          {eventAssertDiagnostics && <EventAssertDiagnosticsTable diag={eventAssertDiagnostics} />}
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

/**
 * Pulls DB-check failure diagnostics out of the loose metadata bag the agent
 * attaches. Returns null when the step isn't a DB check failure (or carries no
 * diagnostics). Consumers render via <see cref="DbDiagnosticsTable"/>.
 *
 * REQ-002 attaches:
 *   - `dbCheckRow`: first row of the failing query (column → value).
 *   - `dbCheckRows`: array of up to 3 rows for row-count failures.
 */
type DbCheckDiagnostics = {
  primaryRow: Record<string, string | null>;
  extraRows: Array<Record<string, string | null>>;
};

function extractDbDiagnostics(step: StepResult): DbCheckDiagnostics | null {
  const meta = step.metadata;
  if (!meta || typeof meta !== 'object') return null;
  const rawRow = (meta as Record<string, unknown>)['dbCheckRow'];
  const rawRows = (meta as Record<string, unknown>)['dbCheckRows'];
  let primary: Record<string, string | null> | null = null;
  if (rawRow && typeof rawRow === 'object') primary = rawRow as Record<string, string | null>;
  let extras: Array<Record<string, string | null>> = [];
  if (Array.isArray(rawRows)) {
    extras = (rawRows as unknown[])
      .filter(r => r && typeof r === 'object')
      .map(r => r as Record<string, string | null>);
  }
  if (!primary && extras.length === 0) return null;
  if (!primary && extras.length > 0) primary = extras[0];
  return { primaryRow: primary!, extraRows: extras.length > 1 ? extras.slice(1) : [] };
}

function DbDiagnosticsTable({ diag }: { diag: DbCheckDiagnostics }) {
  const renderRow = (row: Record<string, string | null>, key: number) => (
    <table key={key} style={{
      borderCollapse: 'collapse', fontSize: 12, marginTop: 8,
      background: '#fff', border: '1px solid #e2e8f0', borderRadius: 4,
    }}>
      <thead>
        <tr>
          {Object.keys(row).map(col => (
            <th key={col} style={{
              padding: '4px 8px', borderBottom: '1px solid #e2e8f0',
              textAlign: 'left', fontSize: 11, fontWeight: 600, color: '#475569',
            }}>{col}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        <tr>
          {Object.entries(row).map(([col, value]) => (
            <td key={col} style={{
              padding: '4px 8px', borderTop: '1px solid #f1f5f9',
              fontFamily: 'ui-monospace, Consolas, monospace',
              color: value === null ? '#94a3b8' : '#0f172a',
              fontStyle: value === null ? 'italic' : 'normal',
              maxWidth: 220, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>{value === null ? 'NULL' : value}</td>
          ))}
        </tr>
      </tbody>
    </table>
  );

  return (
    <div style={{ marginTop: 8 }}>
      <div style={{ fontSize: 11, fontWeight: 600, color: '#475569', textTransform: 'uppercase', letterSpacing: 0.4 }}>
        Failing row
      </div>
      <div style={{ overflowX: 'auto' }}>{renderRow(diag.primaryRow, 0)}</div>
      {diag.extraRows.length > 0 && (
        <>
          <div style={{ marginTop: 10, fontSize: 11, fontWeight: 600, color: '#475569', textTransform: 'uppercase', letterSpacing: 0.4 }}>
            Additional rows
          </div>
          {diag.extraRows.map((r, i) => (
            <div key={i} style={{ overflowX: 'auto' }}>{renderRow(r, i + 1)}</div>
          ))}
        </>
      )}
    </div>
  );
}

/**
 * Pulls Service Bus event-assert diagnostics out of step.metadata. Returns
 * null when the step isn't an event-assert (or carries no diagnostics).
 *
 * REQ-004 attaches `serviceBusReceived`:
 *   {
 *     totalReceived, passCount, matchMode, expectedCount, maxCount,
 *     messages: [
 *       { index, messageId, correlationId, contentType, enqueuedTimeUtc,
 *         applicationProperties, bodyPreview, bodyFormat, bodyLength,
 *         passed, criteria: [{ field, op, passed, reason }] }
 *     ]
 *   }
 */
type EventAssertDiagnostics = {
  totalReceived: number;
  passCount: number;
  matchMode?: string;
  expectedCount?: number | null;
  maxCount?: number | null;
  messages: Array<EventAssertMessage>;
};

type EventAssertMessage = {
  index: number;
  messageId: string | null;
  correlationId: string | null;
  contentType: string | null;
  enqueuedTimeUtc: string;
  applicationProperties: Record<string, string>;
  bodyPreview: string;
  bodyFormat: string;
  bodyLength: number;
  passed: boolean | null;
  criteria: Array<{ field: string; op: string; passed: boolean; reason: string | null }>;
};

function extractEventAssertDiagnostics(step: StepResult): EventAssertDiagnostics | null {
  const meta = step.metadata;
  if (!meta || typeof meta !== 'object') return null;
  const raw = (meta as Record<string, unknown>)['serviceBusReceived'];
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const messagesRaw = Array.isArray(r['messages']) ? (r['messages'] as unknown[]) : [];
  const messages: EventAssertMessage[] = messagesRaw
    .filter(m => m && typeof m === 'object')
    .map((mu, i) => {
      const m = mu as Record<string, unknown>;
      const criteriaRaw = Array.isArray(m['criteria']) ? (m['criteria'] as unknown[]) : [];
      const apps = m['applicationProperties'] && typeof m['applicationProperties'] === 'object'
        ? m['applicationProperties'] as Record<string, string>
        : {};
      return {
        index: typeof m['index'] === 'number' ? (m['index'] as number) : i,
        messageId: typeof m['messageId'] === 'string' ? (m['messageId'] as string) : null,
        correlationId: typeof m['correlationId'] === 'string' ? (m['correlationId'] as string) : null,
        contentType: typeof m['contentType'] === 'string' ? (m['contentType'] as string) : null,
        enqueuedTimeUtc: typeof m['enqueuedTimeUtc'] === 'string' ? (m['enqueuedTimeUtc'] as string) : '',
        applicationProperties: apps,
        bodyPreview: typeof m['bodyPreview'] === 'string' ? (m['bodyPreview'] as string) : '',
        bodyFormat: typeof m['bodyFormat'] === 'string' ? (m['bodyFormat'] as string) : '',
        bodyLength: typeof m['bodyLength'] === 'number' ? (m['bodyLength'] as number) : 0,
        passed: typeof m['passed'] === 'boolean' ? (m['passed'] as boolean) : null,
        criteria: criteriaRaw
          .filter(c => c && typeof c === 'object')
          .map(cu => {
            const c = cu as Record<string, unknown>;
            return {
              field: typeof c['field'] === 'string' ? (c['field'] as string) : '',
              op: typeof c['op'] === 'string' ? (c['op'] as string) : '',
              passed: typeof c['passed'] === 'boolean' ? (c['passed'] as boolean) : false,
              reason: typeof c['reason'] === 'string' ? (c['reason'] as string) : null,
            };
          }),
      };
    });
  return {
    totalReceived: typeof r['totalReceived'] === 'number' ? (r['totalReceived'] as number) : messages.length,
    passCount: typeof r['passCount'] === 'number' ? (r['passCount'] as number) : 0,
    matchMode: typeof r['matchMode'] === 'string' ? (r['matchMode'] as string) : undefined,
    expectedCount: r['expectedCount'] === null || typeof r['expectedCount'] === 'number'
      ? (r['expectedCount'] as number | null) : undefined,
    maxCount: r['maxCount'] === null || typeof r['maxCount'] === 'number'
      ? (r['maxCount'] as number | null) : undefined,
    messages,
  };
}

function EventAssertDiagnosticsTable({ diag }: { diag: EventAssertDiagnostics }) {
  return (
    <div style={{ marginTop: 8 }}>
      <div style={{ fontSize: 11, fontWeight: 600, color: '#475569', textTransform: 'uppercase', letterSpacing: 0.4 }}>
        Service Bus messages received
      </div>
      <div style={{ fontSize: 11, color: '#64748b', marginTop: 4, marginBottom: 6 }}>
        {diag.totalReceived} received · {diag.passCount} matched
        {diag.matchMode && ` · mode ${diag.matchMode}`}
        {(diag.expectedCount !== null && diag.expectedCount !== undefined)
          && ` · expected ${diag.expectedCount}`}
        {(diag.maxCount !== null && diag.maxCount !== undefined)
          && ` · max ${diag.maxCount}`}
      </div>
      {diag.messages.length === 0 && (
        <div style={{ fontSize: 12, color: '#94a3b8', fontStyle: 'italic' }}>
          No messages received within the timeout window.
        </div>
      )}
      {diag.messages.map(m => (
        <EventAssertMessageRow key={m.index} m={m} />
      ))}
    </div>
  );
}

function EventAssertMessageRow({ m }: { m: EventAssertMessage }) {
  const [expanded, setExpanded] = useState(false);
  const tint = m.passed === true ? '#16a34a' : m.passed === false ? '#dc2626' : '#475569';
  const summary =
    `[${m.index}] ${m.messageId ?? '(no id)'}`
    + (m.correlationId ? ` · corr=${m.correlationId}` : '')
    + (m.contentType ? ` · ${m.contentType}` : '');

  return (
    <div style={{
      border: '1px solid #e2e8f0', borderRadius: 4, padding: 6,
      marginBottom: 4, background: '#fff',
    }}>
      <button
        onClick={() => setExpanded(v => !v)}
        style={{
          background: 'transparent', border: 'none', padding: 0, cursor: 'pointer',
          textAlign: 'left', color: tint, fontSize: 12,
          fontFamily: 'ui-monospace,Consolas,monospace', fontWeight: 600,
        }}
      >
        {expanded ? '▾' : '▸'} {m.passed === true ? '✓' : m.passed === false ? '✗' : '·'} {summary}
      </button>
      {expanded && (
        <div style={{ marginTop: 6, fontSize: 11, color: '#475569' }}>
          <div>
            <strong>enqueued:</strong> {m.enqueuedTimeUtc}
            <span style={{ marginLeft: 10 }}>
              <strong>body:</strong> {m.bodyFormat} ({m.bodyLength} bytes)
            </span>
          </div>
          {Object.keys(m.applicationProperties).length > 0 && (
            <div style={{ marginTop: 4 }}>
              <strong>ApplicationProperties:</strong>{' '}
              {Object.entries(m.applicationProperties).map(([k, v], i, arr) => (
                <span key={k} style={{ fontFamily: 'ui-monospace,Consolas,monospace', color: '#0f172a' }}>
                  {k}={v}{i < arr.length - 1 ? ', ' : ''}
                </span>
              ))}
            </div>
          )}
          {m.criteria.length > 0 && (
            <div style={{ marginTop: 4 }}>
              <strong>criteria:</strong>
              <ul style={{ margin: '2px 0 0 16px', padding: 0 }}>
                {m.criteria.map((c, i) => (
                  <li key={i} style={{
                    fontFamily: 'ui-monospace,Consolas,monospace',
                    color: c.passed ? '#16a34a' : '#dc2626',
                  }}>
                    {c.passed ? '✓' : '✗'} {c.field} {c.op}
                    {c.reason && !c.passed ? ` — ${c.reason}` : ''}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {m.bodyPreview && (
            <pre style={{
              margin: '6px 0 0', padding: '4px 6px', background: '#f8fafc',
              border: '1px solid #e2e8f0', borderRadius: 3, fontSize: 11,
              whiteSpace: 'pre-wrap', wordBreak: 'break-word',
              maxHeight: 200, overflow: 'auto',
            }}>{m.bodyPreview}</pre>
          )}
        </div>
      )}
    </div>
  );
}

// Keep this hook exported so any future surface can reuse it
export { extractDueTime };
