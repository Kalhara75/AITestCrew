import { Fragment, useState } from 'react';
import type { PostStep, WebUiStep, DesktopUiStep } from '../types';
import { deletePostStep, updatePostStep } from '../api/modules';
import type { PostStepParentKind } from '../api/modules';
import { EditWebUiTestCaseDialog } from './EditWebUiTestCaseDialog';
import { EditDesktopUiTestCaseDialog } from './EditDesktopUiTestCaseDialog';

interface Props {
  /** The parent step type whose post-steps are being rendered. */
  parentKind: PostStepParentKind;
  /** 0-based index into the parent step list on the objective. */
  parentIndex: number;
  /** Test objective id containing this parent step. */
  objectiveId: string;
  /** Display label used in the panel header (usually the objective name). */
  caseName: string;
  /** The post-step list (possibly empty — panel returns null when so). */
  postSteps: PostStep[];
  moduleId?: string;
  testSetId?: string;
  onChanged?: () => void;
}

/**
 * Reusable panel that renders the post-step list of ANY parent step type
 * (Web UI, Desktop UI, API, aseXML generate, aseXML deliver). Edit/delete
 * go through the generalized /post-steps/{parentKind}/{parentIndex}/{postIndex}
 * endpoints.
 *
 * aseXML delivery keeps its own legacy VerificationsPanel in
 * AseXmlDeliveryTestCaseTable for now because it predates this component
 * and uses the legacy deliveries/verifications routes.
 */
export function PostStepsPanel({
  parentKind, parentIndex, objectiveId, caseName, postSteps,
  moduleId, testSetId, onChanged,
}: Props) {
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [confirmDeleteIdx, setConfirmDeleteIdx] = useState<number | null>(null);
  const [deletingIdx, setDeletingIdx] = useState<number | null>(null);
  const [editingIdx, setEditingIdx] = useState<number | null>(null);

  if (!postSteps || postSteps.length === 0) return null;

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
      await deletePostStep(moduleId, testSetId, objectiveId, parentKind, parentIndex, i);
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
        Post-steps for <span style={{ color: '#1e293b' }}>{caseName}</span>
        <span style={{ color: '#94a3b8', fontWeight: 400 }}>
          {' '}— {postSteps.length} post-step{postSteps.length !== 1 ? 's' : ''} ({parentKind}[{parentIndex}])
        </span>
      </div>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={{ ...thS, width: 24 }}></th>
            <th style={thS}>#</th>
            <th style={thS}>Target</th>
            <th style={thS}>Description</th>
            <th style={thS}>Wait</th>
            <th style={thS}>Payload</th>
            {canEdit && <th style={{ ...thS, width: 60 }}></th>}
          </tr>
        </thead>
        <tbody>
          {postSteps.map((p, i) => {
            const expandable = !!(p.webUi || p.desktopUi || p.dbCheck);
            const isExpanded = expanded.has(i);
            return (
              <Fragment key={i}>
                <tr
                  style={{
                    borderTop: '1px solid #e2e8f0',
                    cursor: expandable ? 'pointer' : 'default',
                    background: isExpanded ? '#eef2ff' : 'transparent',
                  }}
                  onClick={() => expandable && toggleExpand(i)}
                >
                  <td style={{ ...tdS, textAlign: 'center', color: '#64748b' }}>
                    {expandable ? (isExpanded ? '\u25BE' : '\u25B8') : ''}
                  </td>
                  <td style={tdS}>{i + 1}</td>
                  <td style={tdS}><span style={targetBadge(p.target)}>{p.target}</span></td>
                  <td style={tdS}>{p.description}</td>
                  <td style={tdS}>{p.waitBeforeSeconds}s</td>
                  <td style={{ ...tdS, fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12, color: '#64748b' }}>
                    <PayloadSummary p={p} />
                  </td>
                  {canEdit && (
                    <td style={{ ...tdS, whiteSpace: 'nowrap' }} onClick={e => e.stopPropagation()}>
                      {confirmDeleteIdx === i ? (
                        <span style={{ display: 'inline-flex', gap: 4 }}>
                          <button onClick={() => handleDelete(i)} disabled={deletingIdx === i} style={confirmBtn}>
                            {deletingIdx === i ? '...' : 'Yes'}
                          </button>
                          <button onClick={() => setConfirmDeleteIdx(null)} style={cancelBtn}>No</button>
                        </span>
                      ) : (
                        <>
                          {(p.webUi || p.desktopUi) && (
                            <span
                              onClick={() => setEditingIdx(i)}
                              style={{ fontSize: 13, color: '#1d4ed8', cursor: 'pointer', opacity: 0.7, marginRight: 8 }}
                              title={p.desktopUi ? 'Edit Desktop UI steps' : 'Edit Web UI steps'}
                            >
                              &#9998;
                            </span>
                          )}
                          <span
                            onClick={() => setConfirmDeleteIdx(i)}
                            style={{ fontSize: 13, color: '#dc2626', cursor: 'pointer', opacity: 0.6 }}
                            title="Delete post-step"
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
                      {p.webUi && <WebUiStepsTable steps={p.webUi.steps} startUrl={p.webUi.startUrl} />}
                      {p.desktopUi && <DesktopUiStepsTable steps={p.desktopUi.steps} />}
                      {p.dbCheck && <DbCheckBlock dc={p.dbCheck} />}
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>

      {editingIdx !== null && moduleId && testSetId && postSteps[editingIdx].webUi && (
        <EditWebUiTestCaseDialog
          open
          title="Edit Post-Step — Web UI"
          definition={postSteps[editingIdx].webUi!}
          caseName={postSteps[editingIdx].description}
          deleteLabel="Delete Post-Step"
          deleteConfirmMessage="Delete this post-step?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: PostStep = { ...postSteps[editingIdx], description: name, webUi: definition };
            await updatePostStep(moduleId, testSetId, objectiveId, parentKind, parentIndex, editingIdx, updated);
            setEditingIdx(null);
            onChanged?.();
          }}
          onDelete={async () => {
            await deletePostStep(moduleId, testSetId, objectiveId, parentKind, parentIndex, editingIdx);
            setEditingIdx(null);
            onChanged?.();
          }}
        />
      )}

      {editingIdx !== null && moduleId && testSetId && postSteps[editingIdx].desktopUi && !postSteps[editingIdx].webUi && (
        <EditDesktopUiTestCaseDialog
          open
          title="Edit Post-Step — Desktop UI"
          definition={postSteps[editingIdx].desktopUi!}
          caseName={postSteps[editingIdx].description}
          deleteLabel="Delete Post-Step"
          deleteConfirmMessage="Delete this post-step?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: PostStep = { ...postSteps[editingIdx], description: name, desktopUi: definition };
            await updatePostStep(moduleId, testSetId, objectiveId, parentKind, parentIndex, editingIdx, updated);
            setEditingIdx(null);
            onChanged?.();
          }}
          onDelete={async () => {
            await deletePostStep(moduleId, testSetId, objectiveId, parentKind, parentIndex, editingIdx);
            setEditingIdx(null);
            onChanged?.();
          }}
        />
      )}
    </div>
  );
}

function PayloadSummary({ p }: { p: PostStep }) {
  if (p.webUi) {
    const n = p.webUi.steps?.length ?? 0;
    return <>{n} web step{n !== 1 ? 's' : ''}</>;
  }
  if (p.desktopUi) {
    const n = p.desktopUi.steps?.length ?? 0;
    return <>{n} desktop step{n !== 1 ? 's' : ''}</>;
  }
  if (p.dbCheck) {
    const expect = p.dbCheck.expectedRowCount !== undefined
      ? `rows=${p.dbCheck.expectedRowCount}`
      : (p.dbCheck.expectedColumnValues && Object.keys(p.dbCheck.expectedColumnValues).length > 0)
        ? `cols=${Object.keys(p.dbCheck.expectedColumnValues).length}` : '(no expectations)';
    return <>SQL / {expect}</>;
  }
  if (p.api) return <>{p.api.method} {p.api.endpoint}</>;
  if (p.aseXmlDeliver) return <>deliver {p.aseXmlDeliver.templateId} → {p.aseXmlDeliver.endpointCode}</>;
  if (p.aseXml) return <>render {p.aseXml.templateId}</>;
  return <em style={{ color: '#94a3b8' }}>(no payload)</em>;
}

function DbCheckBlock({ dc }: { dc: NonNullable<PostStep['dbCheck']> }) {
  return (
    <div style={{ marginTop: 6, fontSize: 12 }}>
      <div style={{ color: '#64748b', marginBottom: 4 }}>
        <strong>connection:</strong> <code style={inlineCode}>{dc.connectionKey}</code>
        <span style={{ marginLeft: 10 }}>
          <strong>timeout:</strong> {dc.timeoutSeconds}s
        </span>
      </div>
      <div style={{ color: '#64748b', marginBottom: 4 }}>
        <strong>sql:</strong>
        <pre style={{
          margin: '4px 0', padding: '6px 8px', background: '#fff', border: '1px solid #e2e8f0',
          borderRadius: 4, fontSize: 12, whiteSpace: 'pre-wrap', wordBreak: 'break-word',
        }}>{highlightTokensStr(dc.sql)}</pre>
      </div>
      {dc.expectedRowCount !== undefined && (
        <div style={{ color: '#64748b' }}>
          <strong>expected row count:</strong> {dc.expectedRowCount}
        </div>
      )}
      {dc.expectedColumnValues && Object.keys(dc.expectedColumnValues).length > 0 && (
        <div style={{ color: '#64748b', marginTop: 4 }}>
          <strong>expected column values:</strong>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
            {Object.entries(dc.expectedColumnValues).map(([k, v]) => (
              <span key={k} style={{
                fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12,
                background: '#eef2ff', border: '1px solid #c7d2fe', borderRadius: 3, padding: '2px 6px',
              }}>
                <strong style={{ color: '#4f46e5' }}>{k}</strong>={v}
              </span>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

function WebUiStepsTable({ steps, startUrl }: { steps: WebUiStep[]; startUrl?: string }) {
  if (!steps || steps.length === 0) {
    return <span style={{ color: '#64748b', fontSize: 12 }}>No Playwright steps captured.</span>;
  }
  return (
    <div style={{ marginTop: 6 }}>
      {startUrl && (
        <div style={{ fontSize: 12, color: '#64748b', marginBottom: 6 }}>
          <strong>Start URL:</strong> <code style={inlineCode}>{highlightTokens(startUrl)}</code>
        </div>
      )}
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={stepTh}>#</th>
            <th style={stepTh}>Action</th>
            <th style={stepTh}>Selector</th>
            <th style={stepTh}>Value</th>
            <th style={stepTh}>Timeout</th>
          </tr>
        </thead>
        <tbody>
          {steps.map((s, i) => (
            <tr key={i} style={{ borderTop: '1px solid #e2e8f0' }}>
              <td style={stepTd}>{i + 1}</td>
              <td style={stepTd}><strong>{s.action}</strong></td>
              <td style={stepTd}><code style={inlineCode}>{highlightTokens(s.selector ?? '')}</code></td>
              <td style={stepTd}><code style={inlineCode}>{highlightTokens(s.value ?? '')}</code></td>
              <td style={stepTd}>{s.timeoutMs}ms</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function DesktopUiStepsTable({ steps }: { steps: DesktopUiStep[] }) {
  if (!steps || steps.length === 0) {
    return <span style={{ color: '#64748b', fontSize: 12 }}>No desktop steps captured.</span>;
  }
  return (
    <div style={{ marginTop: 6 }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
        <thead>
          <tr style={{ textAlign: 'left', color: '#64748b' }}>
            <th style={stepTh}>#</th>
            <th style={stepTh}>Action</th>
            <th style={stepTh}>Selector</th>
            <th style={stepTh}>Value</th>
            <th style={stepTh}>Timeout</th>
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
                <td style={stepTd}>{i + 1}</td>
                <td style={stepTd}><strong>{s.action}</strong></td>
                <td style={stepTd}><code style={inlineCode}>{highlightTokens(selectorLabel)}</code></td>
                <td style={stepTd}><code style={inlineCode}>{highlightTokens(s.value ?? '')}</code></td>
                <td style={stepTd}>{s.timeoutMs}ms</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

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

// Same logic but returning just a string, used inside <pre> where we render
// plain text (no React formatting).
function highlightTokensStr(text: string): string {
  return text ?? '';
}

function targetBadge(target: string): React.CSSProperties {
  const palette = target.includes('Desktop')
    ? { bg: '#ecfdf5', fg: '#047857', border: '#a7f3d0' }
    : target.includes('Blazor') || target.includes('Web_Blazor')
    ? { bg: '#eff6ff', fg: '#1d4ed8', border: '#bfdbfe' }
    : target.includes('MVC')
    ? { bg: '#fef3c7', fg: '#92400e', border: '#fde68a' }
    : target.includes('Db_')
    ? { bg: '#fae8ff', fg: '#86198f', border: '#f5d0fe' }
    : target.includes('API')
    ? { bg: '#fef2f2', fg: '#991b1b', border: '#fecaca' }
    : { bg: '#f1f5f9', fg: '#334155', border: '#cbd5e1' };
  return {
    fontSize: 11, fontWeight: 600, padding: '2px 8px', borderRadius: 4,
    background: palette.bg, color: palette.fg, border: `1px solid ${palette.border}`,
  };
}

const thS: React.CSSProperties = {
  padding: '6px 10px', fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5,
};
const tdS: React.CSSProperties = {
  padding: '6px 10px', color: '#1e293b', verticalAlign: 'top',
};
const stepTh: React.CSSProperties = {
  padding: '4px 8px', fontSize: 10, fontWeight: 600, textTransform: 'uppercase', letterSpacing: 0.5, color: '#64748b',
};
const stepTd: React.CSSProperties = {
  padding: '4px 8px', color: '#0f172a', verticalAlign: 'top',
};
const inlineCode: React.CSSProperties = {
  fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 11, color: '#334155',
  background: '#fff', border: '1px solid #e2e8f0', padding: '1px 4px', borderRadius: 3,
  wordBreak: 'break-all',
};
const confirmBtn: React.CSSProperties = {
  background: '#dc2626', color: '#fff', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};
const cancelBtn: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', borderRadius: 3,
  padding: '1px 6px', fontSize: 11, fontWeight: 600, cursor: 'pointer',
};
