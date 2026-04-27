import { Fragment, useState } from 'react';
import { deleteVerification, updateVerification } from '../api/modules';
import { EditWebUiTestCaseDialog } from './EditWebUiTestCaseDialog';
import { EditDesktopUiTestCaseDialog } from './EditDesktopUiTestCaseDialog';
import type { TestObjective, AseXmlDeliveryTestDefinition, VerificationStep, WebUiStep, DesktopUiStep } from '../types';

interface Props {
  objectives: TestObjective[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

/**
 * Read-only viewer for aseXML DELIVERY test cases. Shows one row per delivery case
 * with the Endpoint column; when a delivery has PostDeliveryVerifications, they
 * render as a nested panel beneath the row (Phase 3). Verifications can be expanded
 * to see their individual UI steps, and deleted (re-recording via CLI).
 */
export function AseXmlDeliveryTestCaseTable({ objectives, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const allCases = objectives
    .filter(o => o.aseXmlDeliverySteps && o.aseXmlDeliverySteps.length > 0)
    .flatMap(o => o.aseXmlDeliverySteps.map((step, idx) => ({
      step,
      objectiveId: o.id,
      objectiveName: o.name,
      stepIndex: idx,
      key: `${o.id}-asexml-deliver-${idx}`,
    })));

  if (allCases.length === 0) {
    return <p style={{ color: '#94a3b8', fontSize: 14, padding: '8px 0' }}>No aseXML delivery test cases.</p>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Transaction</th>
            <th style={thStyle}>Template</th>
            <th style={thStyle}>Endpoint</th>
            <th style={thStyle}>Field Values</th>
          </tr>
        </thead>
        <tbody>
          {allCases.map(tc => (
            <tr key={tc.key} style={{ borderBottom: '1px solid #f1f5f9' }}>
              <td style={tdStyle}>
                <div>{tc.objectiveName}</div>
                {tc.step.description && tc.step.description !== tc.objectiveName && (
                  <div style={{ color: '#64748b', fontSize: 12, marginTop: 2 }}>{tc.step.description}</div>
                )}
              </td>
              <td style={tdStyle}>
                <span style={{
                  fontFamily: 'ui-monospace, Consolas, monospace',
                  fontSize: 13,
                  color: '#1e40af',
                }}>
                  {tc.step.transactionType || '(n/a)'}
                </span>
              </td>
              <td style={tdStyle}>
                <span style={{
                  fontFamily: 'ui-monospace, Consolas, monospace',
                  fontSize: 12,
                  color: '#334155',
                  background: '#f1f5f9',
                  padding: '2px 6px',
                  borderRadius: 3,
                }}>
                  {tc.step.templateId}
                </span>
              </td>
              <td style={tdStyle}>
                {tc.step.endpointCode ? (
                  <span style={{
                    fontFamily: 'ui-monospace, Consolas, monospace',
                    fontSize: 12,
                    color: '#831843',
                    background: '#fdf2f8',
                    border: '1px solid #fbcfe8',
                    padding: '2px 6px',
                    borderRadius: 3,
                    fontWeight: 600,
                  }}>
                    {tc.step.endpointCode}
                  </span>
                ) : (
                  <span style={{ color: '#94a3b8', fontSize: 13 }}>
                    <em>no default</em> — pass <code>--endpoint</code> at run
                  </span>
                )}
              </td>
              <td style={tdStyle}>
                <FieldValuePreview step={tc.step} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {allCases.some(tc => ((tc.step.postSteps ?? tc.step.postDeliveryVerifications)?.length ?? 0) > 0) && (
        <div style={{ marginTop: 16 }}>
          {allCases.map(tc =>
            ((tc.step.postSteps ?? tc.step.postDeliveryVerifications)?.length ?? 0) > 0 && (
              <VerificationsPanel
                key={`${tc.key}-verifs`}
                caseName={tc.objectiveName}
                objectiveId={tc.objectiveId}
                deliveryIndex={tc.stepIndex}
                verifications={(tc.step.postSteps ?? tc.step.postDeliveryVerifications)}
                moduleId={moduleId}
                testSetId={testSetId}
                onChanged={onTestCaseUpdated}
              />
            )
          )}
        </div>
      )}
    </div>
  );
}

interface VerificationsPanelProps {
  caseName: string;
  objectiveId: string;
  deliveryIndex: number;
  verifications: VerificationStep[];
  moduleId?: string;
  testSetId?: string;
  onChanged?: () => void;
}

function VerificationsPanel({
  caseName, objectiveId, deliveryIndex, verifications,
  moduleId, testSetId, onChanged,
}: VerificationsPanelProps) {
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [confirmDeleteIdx, setConfirmDeleteIdx] = useState<number | null>(null);
  const [deletingIdx, setDeletingIdx] = useState<number | null>(null);
  const [editingIdx, setEditingIdx] = useState<number | null>(null);

  const canEdit = !!moduleId && !!testSetId;

  const toggleExpand = (i: number) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(i)) next.delete(i); else next.add(i);
      return next;
    });
  };

  const handleDelete = async (i: number) => {
    if (!moduleId || !testSetId) return;
    setDeletingIdx(i);
    try {
      await deleteVerification(moduleId, testSetId, objectiveId, deliveryIndex, i);
      setConfirmDeleteIdx(null);
      onChanged?.();
    } catch {
      setDeletingIdx(null);
    }
  };

  return (
    <div style={{
      background: '#f8fafc',
      border: '1px solid #e2e8f0',
      borderRadius: 6,
      padding: '10px 14px',
      marginTop: 8,
    }}>
      <div style={{ fontSize: 12, color: '#64748b', fontWeight: 600, marginBottom: 6 }}>
        Post-delivery UI verifications for <span style={{ color: '#1e293b' }}>{caseName}</span>
        <span style={{ color: '#94a3b8', fontWeight: 400 }}> — {verifications.length} verification{verifications.length !== 1 ? 's' : ''}</span>
      </div>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={{ ...verifThStyle, width: 24 }}></th>
            <th style={verifThStyle}>#</th>
            <th style={verifThStyle}>Target</th>
            <th style={verifThStyle}>Description</th>
            <th style={verifThStyle}>Wait</th>
            <th style={verifThStyle}>Steps</th>
            {canEdit && <th style={{ ...verifThStyle, width: 60 }}></th>}
          </tr>
        </thead>
        <tbody>
          {verifications.map((v, i) => {
            const steps = (v.webUi?.steps ?? v.desktopUi?.steps ?? []) as Array<{ action: string }>;
            const isExpanded = expanded.has(i);
            return (
              <Fragment key={i}>
                <tr
                  style={{
                    borderTop: '1px solid #e2e8f0',
                    cursor: steps.length > 0 ? 'pointer' : 'default',
                    background: isExpanded ? '#eef2ff' : 'transparent',
                  }}
                  onClick={() => steps.length > 0 && toggleExpand(i)}
                >
                  <td style={{ ...verifTdStyle, textAlign: 'center', color: '#64748b' }}>
                    {steps.length > 0 ? (isExpanded ? '\u25BE' : '\u25B8') : ''}
                  </td>
                  <td style={verifTdStyle}>{i + 1}</td>
                  <td style={verifTdStyle}>
                    <span style={targetBadgeStyle(v.target)}>{v.target}</span>
                  </td>
                  <td style={verifTdStyle}>{v.description}</td>
                  <td style={verifTdStyle}>{v.waitBeforeSeconds}s</td>
                  <td style={{ ...verifTdStyle, fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12, color: '#64748b' }}>
                    {steps.length} &nbsp;
                    {steps.slice(0, 3).map((s, idx) => (
                      <span key={idx}>
                        {s.action}
                        {idx < Math.min(steps.length, 3) - 1 ? ' \u2192 ' : ''}
                      </span>
                    ))}
                    {steps.length > 3 && <span> +{steps.length - 3}</span>}
                  </td>
                  {canEdit && (
                    <td
                      style={{ ...verifTdStyle, whiteSpace: 'nowrap' }}
                      onClick={e => e.stopPropagation()}
                    >
                      {confirmDeleteIdx === i ? (
                        <span style={{ display: 'inline-flex', gap: 4 }}>
                          <button
                            onClick={() => handleDelete(i)}
                            disabled={deletingIdx === i}
                            style={inlineDeleteConfirmStyle}
                          >
                            {deletingIdx === i ? '...' : 'Yes'}
                          </button>
                          <button
                            onClick={() => setConfirmDeleteIdx(null)}
                            style={inlineCancelStyle}
                          >
                            No
                          </button>
                        </span>
                      ) : (
                        <>
                          {(v.webUi || v.desktopUi) && (
                            <span
                              onClick={() => setEditingIdx(i)}
                              style={{ fontSize: 13, color: '#1d4ed8', cursor: 'pointer', opacity: 0.7, marginRight: 8 }}
                              title={v.desktopUi ? 'Edit Desktop UI steps' : 'Edit Web UI steps'}
                            >
                              &#9998;
                            </span>
                          )}
                          <span
                            onClick={() => setConfirmDeleteIdx(i)}
                            style={{ fontSize: 13, color: '#dc2626', cursor: 'pointer', opacity: 0.6 }}
                            title="Delete verification"
                          >
                            &#128465;
                          </span>
                        </>
                      )}
                    </td>
                  )}
                </tr>
                {isExpanded && (
                  <tr>
                    <td colSpan={canEdit ? 7 : 6} style={{ padding: '0 10px 12px 40px', background: '#eef2ff' }}>
                      {v.webUi && <WebUiStepsTable steps={v.webUi.steps} startUrl={v.webUi.startUrl} />}
                      {v.desktopUi && <DesktopUiStepsTable steps={v.desktopUi.steps} />}
                      {!v.webUi && !v.desktopUi && (
                        <span style={{ color: '#64748b', fontSize: 12 }}>
                          This verification has no recorded steps. Delete and re-record.
                        </span>
                      )}
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>

      {editingIdx !== null && moduleId && testSetId && verifications[editingIdx].webUi && (
        <EditWebUiTestCaseDialog
          open
          title="Edit Verification — Web UI Steps"
          definition={verifications[editingIdx].webUi!}
          caseName={verifications[editingIdx].description}
          deleteLabel="Delete Verification"
          deleteConfirmMessage="Delete this entire verification?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: VerificationStep = {
              ...verifications[editingIdx],
              description: name,
              webUi: definition,
            };
            await updateVerification(moduleId, testSetId, objectiveId, deliveryIndex, editingIdx, updated);
            setEditingIdx(null);
            onChanged?.();
          }}
          onDelete={async () => {
            await deleteVerification(moduleId, testSetId, objectiveId, deliveryIndex, editingIdx);
            setEditingIdx(null);
            onChanged?.();
          }}
        />
      )}

      {editingIdx !== null && moduleId && testSetId && verifications[editingIdx].desktopUi && !verifications[editingIdx].webUi && (
        <EditDesktopUiTestCaseDialog
          open
          title="Edit Verification — Desktop UI Steps"
          definition={verifications[editingIdx].desktopUi!}
          caseName={verifications[editingIdx].description}
          deleteLabel="Delete Verification"
          deleteConfirmMessage="Delete this entire verification?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: VerificationStep = {
              ...verifications[editingIdx],
              description: name,
              desktopUi: definition,
            };
            await updateVerification(moduleId, testSetId, objectiveId, deliveryIndex, editingIdx, updated);
            setEditingIdx(null);
            onChanged?.();
          }}
          onDelete={async () => {
            await deleteVerification(moduleId, testSetId, objectiveId, deliveryIndex, editingIdx);
            setEditingIdx(null);
            onChanged?.();
          }}
        />
      )}
    </div>
  );
}

function WebUiStepsTable({ steps, startUrl }: { steps: WebUiStep[]; startUrl?: string }) {
  if (steps.length === 0) {
    return <span style={{ color: '#64748b', fontSize: 12 }}>No Playwright steps captured.</span>;
  }
  return (
    <div style={{ marginTop: 6 }}>
      {startUrl && (
        <div style={{ fontSize: 12, color: '#64748b', marginBottom: 6 }}>
          <strong>Start URL:</strong> <code style={inlineCodeStyle}>{highlightTokens(startUrl)}</code>
        </div>
      )}
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={stepThStyle}>#</th>
            <th style={stepThStyle}>Action</th>
            <th style={stepThStyle}>Selector</th>
            <th style={stepThStyle}>Value</th>
            <th style={stepThStyle}>Timeout</th>
          </tr>
        </thead>
        <tbody>
          {steps.map((s, i) => (
            <tr key={i} style={{ borderTop: '1px solid #e2e8f0' }}>
              <td style={stepTdStyle}>{i + 1}</td>
              <td style={stepTdStyle}><strong>{s.action}</strong></td>
              <td style={stepTdStyle}>
                <code style={inlineCodeStyle}>{highlightTokens(s.selector ?? '')}</code>
              </td>
              <td style={stepTdStyle}>
                <code style={inlineCodeStyle}>{highlightTokens(s.value ?? '')}</code>
              </td>
              <td style={stepTdStyle}>{s.timeoutMs}ms</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function DesktopUiStepsTable({ steps }: { steps: DesktopUiStep[] }) {
  if (steps.length === 0) {
    return <span style={{ color: '#64748b', fontSize: 12 }}>No desktop steps captured.</span>;
  }
  return (
    <div style={{ marginTop: 6 }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={stepThStyle}>#</th>
            <th style={stepThStyle}>Action</th>
            <th style={stepThStyle}>Selector</th>
            <th style={stepThStyle}>Value</th>
            <th style={stepThStyle}>Timeout</th>
          </tr>
        </thead>
        <tbody>
          {steps.map((s, i) => {
            const selectorLabel =
              s.automationId ? `AutomationId=${s.automationId}` :
              s.name ? `Name=${s.name}` :
              s.className && s.controlType ? `${s.className}:${s.controlType}` :
              s.treePath ? s.treePath :
              s.menuPath ? `Menu=${s.menuPath}` :
              s.windowTitle ? `Window=${s.windowTitle}` :
              '(none)';
            return (
              <tr key={i} style={{ borderTop: '1px solid #e2e8f0' }}>
                <td style={stepTdStyle}>{i + 1}</td>
                <td style={stepTdStyle}><strong>{s.action}</strong></td>
                <td style={stepTdStyle}>
                  <code style={inlineCodeStyle}>{highlightTokens(selectorLabel)}</code>
                </td>
                <td style={stepTdStyle}>
                  <code style={inlineCodeStyle}>{highlightTokens(s.value ?? '')}</code>
                </td>
                <td style={stepTdStyle}>{s.timeoutMs}ms</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

// Renders a string with {{Token}} substrings visually highlighted so users can
// verify auto-parameterisation worked at a glance.
function highlightTokens(text: string): React.ReactNode {
  if (!text) return <em style={{ color: '#94a3b8' }}>empty</em>;
  const parts = text.split(/(\{\{\s*[A-Za-z_][A-Za-z0-9_]*\s*\}\})/g);
  return parts.map((part, i) =>
    /^\{\{.*\}\}$/.test(part) ? (
      <span key={i} style={{ color: '#4f46e5', fontWeight: 600, background: '#e0e7ff', padding: '0 2px', borderRadius: 2 }}>
        {part}
      </span>
    ) : (
      <span key={i}>{part}</span>
    )
  );
}

function targetBadgeStyle(target: string): React.CSSProperties {
  const palette = target.includes('Desktop')
    ? { bg: '#ecfdf5', fg: '#047857', border: '#a7f3d0' }
    : target.includes('Blazor')
    ? { bg: '#eff6ff', fg: '#1d4ed8', border: '#bfdbfe' }
    : { bg: '#fef3c7', fg: '#92400e', border: '#fde68a' };
  return {
    fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
    background: palette.bg, color: palette.fg, border: `1px solid ${palette.border}`,
  };
}

const verifThStyle: React.CSSProperties = {
  padding: '6px 10px', fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5,
};
const verifTdStyle: React.CSSProperties = {
  padding: '6px 10px', color: '#1e293b', verticalAlign: 'top',
};
const stepThStyle: React.CSSProperties = {
  padding: '4px 8px', fontSize: 10, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5, color: '#64748b',
};
const stepTdStyle: React.CSSProperties = {
  padding: '4px 8px', color: '#0f172a', verticalAlign: 'top',
};
const inlineCodeStyle: React.CSSProperties = {
  fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 11, color: '#334155',
  background: '#fff', border: '1px solid #e2e8f0', padding: '1px 4px', borderRadius: 3,
  wordBreak: 'break-all',
};
const inlineDeleteConfirmStyle: React.CSSProperties = {
  background: '#dc2626', color: '#fff', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};
const inlineCancelStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};

function FieldValuePreview({ step }: { step: AseXmlDeliveryTestDefinition }) {
  const entries = Object.entries(step.fieldValues ?? {});
  if (entries.length === 0) {
    return <span style={{ color: '#94a3b8', fontSize: 13 }}>(no user values)</span>;
  }
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
      {entries.map(([k, v]) => (
        <span
          key={k}
          title={`${k}: ${v}`}
          style={{
            fontFamily: 'ui-monospace, Consolas, monospace',
            fontSize: 12,
            color: '#0f172a',
            background: '#eef2ff',
            border: '1px solid #c7d2fe',
            borderRadius: 3,
            padding: '2px 6px',
          }}
        >
          <strong style={{ color: '#4f46e5' }}>{k}</strong>={v || <em style={{ color: '#94a3b8' }}>empty</em>}
        </span>
      ))}
    </div>
  );
}

const thStyle: React.CSSProperties = {
  padding: '10px 14px',
  color: '#64748b',
  fontWeight: 600,
  fontSize: 12,
  textTransform: 'uppercase',
  letterSpacing: 0.5,
};
const tdStyle: React.CSSProperties = { padding: '10px 14px', color: '#1e293b', verticalAlign: 'top' };
