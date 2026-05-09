import { Fragment, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { AseXmlVerificationConfigResponse, PostStep, WebUiStep, DesktopUiStep } from '../types';
import { deletePostStep, updatePostStep } from '../api/modules';
import type { PostStepParentKind } from '../api/modules';
import { fetchAseXmlVerificationConfig } from '../api/config';
import { triggerRun } from '../api/runs';
import { useActiveRun } from '../contexts/ActiveRunContext';
import { EditWebUiTestCaseDialog } from './EditWebUiTestCaseDialog';
import { EditDesktopUiTestCaseDialog } from './EditDesktopUiTestCaseDialog';
import { EditDbCheckStepDialog } from './EditDbCheckStepDialog';
import { EditEventAssertStepDialog } from './EditEventAssertStepDialog';
import { ExecutionModeBadge } from './execution/ExecutionModeBadge';
import { TargetBadge } from './execution/TargetBadge';

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
  /** Active customer environment key for the DB-check editor's connection dropdown + dry-run. */
  environmentKey?: string | null;
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
  moduleId, testSetId, environmentKey, onChanged,
}: Props) {
  const [expanded, setExpanded] = useState<Set<number>>(new Set());
  const [confirmDeleteIdx, setConfirmDeleteIdx] = useState<number | null>(null);
  const [deletingIdx, setDeletingIdx] = useState<number | null>(null);
  const [editingIdx, setEditingIdx] = useState<number | null>(null);
  const [runErrorIdx, setRunErrorIdx] = useState<{ idx: number; message: string } | null>(null);
  const { individualRun, setIndividualRun } = useActiveRun();
  const anyRunning = !!individualRun;

  const { data: verifConfig } = useQuery({
    queryKey: ['config', 'asexml-verification'],
    queryFn: fetchAseXmlVerificationConfig,
    staleTime: Infinity,
  });

  if (!postSteps || postSteps.length === 0) return null;

  const canEdit = !!moduleId && !!testSetId;
  const canRun = !!testSetId;
  const isDeferred = computeIsDeferred(postSteps, verifConfig);

  const handleRunStep = async (i: number) => {
    if (!testSetId) return;
    setRunErrorIdx(null);
    try {
      const res = await triggerRun({
        mode: 'VerifyOnly',
        testSetId,
        moduleId,
        objectiveId,
        verificationWaitOverride: 0,
        verifyStepFilter: {
          parentKind,
          parentStepIndex: parentIndex,
          postStepIndex: i,
        },
      });
      setIndividualRun({ runId: res.runId, testSetId, moduleId, objectiveId });
    } catch (err) {
      setRunErrorIdx({ idx: i, message: err instanceof Error ? err.message : 'Failed to trigger' });
    }
  };

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
            <th style={thS}>Mode</th>
            <th style={thS}>Payload</th>
            {(canEdit || canRun) && <th style={{ ...thS, width: 100 }}></th>}
          </tr>
        </thead>
        <tbody>
          {postSteps.map((p, i) => {
            const expandable = !!(p.webUi || p.desktopUi || p.dbCheck || p.eventAssert);
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
                  <td style={tdS}><TargetBadge target={p.target} /></td>
                  <td style={tdS}>{p.description}</td>
                  <td style={tdS}>{p.waitBeforeSeconds}s</td>
                  <td style={tdS}>
                    <ExecutionModeBadge
                      deferred={isDeferred}
                      title={
                        verifConfig === undefined
                          ? 'Loading execution mode…'
                          : isDeferred
                          ? `Deferred — at least one post-step in this objective waits > ${verifConfig.verificationDeferThresholdSeconds}s, so all post-steps are queued and run by an agent later.`
                          : 'Inline — all waits are at or below the defer threshold; post-steps run synchronously after the parent step.'
                      }
                    />
                  </td>
                  <td style={{ ...tdS, fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12, color: '#64748b' }}>
                    <PayloadSummary p={p} />
                  </td>
                  {(canEdit || canRun) && (
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
                          {canRun && (
                            <button
                              onClick={() => handleRunStep(i)}
                              disabled={anyRunning}
                              style={{
                                background: 'none',
                                color: anyRunning ? '#94a3b8' : '#0d9488',
                                border: `1px solid ${anyRunning ? '#e2e8f0' : '#ccfbf1'}`,
                                padding: '0px 6px',
                                borderRadius: 3,
                                fontSize: 10,
                                fontWeight: 600,
                                cursor: anyRunning ? 'not-allowed' : 'pointer',
                                marginRight: 6,
                              }}
                              title="Run only this post-step (skips earlier post-steps)"
                            >
                              Run
                            </button>
                          )}
                          {(p.webUi || p.desktopUi || p.dbCheck || p.eventAssert) && canEdit && (
                            <span
                              onClick={() => setEditingIdx(i)}
                              style={{ fontSize: 13, color: '#1d4ed8', cursor: 'pointer', opacity: 0.7, marginRight: 8 }}
                              title={p.desktopUi
                                ? 'Edit Desktop UI steps'
                                : p.dbCheck
                                  ? 'Edit DB check'
                                  : p.eventAssert
                                    ? 'Edit event assertion'
                                    : 'Edit Web UI steps'}
                            >
                              &#9998;
                            </span>
                          )}
                          {canEdit && (
                            <span
                              onClick={() => setConfirmDeleteIdx(i)}
                              style={{ fontSize: 13, color: '#dc2626', cursor: 'pointer', opacity: 0.6 }}
                              title="Delete post-step"
                            >
                              &#128465;
                            </span>
                          )}
                          {runErrorIdx?.idx === i && (
                            <span style={{ color: '#dc2626', fontSize: 10, marginLeft: 6 }} title={runErrorIdx.message}>!</span>
                          )}
                        </>
                      )}
                    </td>
                  )}
                </tr>
                {isExpanded && (
                  <tr>
                    <td colSpan={(canEdit || canRun) ? 8 : 7} style={{ padding: '0 10px 12px 40px', background: '#eef2ff' }}>
                      {p.webUi && <WebUiStepsTable steps={p.webUi.steps} startUrl={p.webUi.startUrl} />}
                      {p.desktopUi && <DesktopUiStepsTable steps={p.desktopUi.steps} />}
                      {p.dbCheck && <DbCheckBlock dc={p.dbCheck} />}
                      {p.eventAssert && <EventAssertBlock ea={p.eventAssert} />}
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

      {editingIdx !== null && moduleId && testSetId
        && postSteps[editingIdx].eventAssert
        && !postSteps[editingIdx].webUi
        && !postSteps[editingIdx].desktopUi
        && !postSteps[editingIdx].dbCheck && (
        <EditEventAssertStepDialog
          open
          title="Edit Post-Step — Event Assertion"
          definition={postSteps[editingIdx].eventAssert!}
          caseName={postSteps[editingIdx].description}
          envKey={environmentKey ?? null}
          deleteLabel="Delete Post-Step"
          deleteConfirmMessage="Delete this post-step?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: PostStep = { ...postSteps[editingIdx], description: name, eventAssert: definition };
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

      {editingIdx !== null && moduleId && testSetId
        && postSteps[editingIdx].dbCheck
        && !postSteps[editingIdx].webUi
        && !postSteps[editingIdx].desktopUi && (
        <EditDbCheckStepDialog
          open
          title="Edit Post-Step — DB Check"
          definition={postSteps[editingIdx].dbCheck!}
          caseName={postSteps[editingIdx].description}
          envKey={environmentKey ?? null}
          deleteLabel="Delete Post-Step"
          deleteConfirmMessage="Delete this post-step?"
          onClose={() => setEditingIdx(null)}
          onSave={async ({ name, definition }) => {
            const updated: PostStep = { ...postSteps[editingIdx], description: name, dbCheck: definition };
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
    const dc = p.dbCheck;
    const assertionCount = dc.columnAssertions?.length ?? 0;
    const captureCount = dc.captures?.length ?? 0;
    const legacyCount = dc.expectedColumnValues
      ? Object.keys(dc.expectedColumnValues).length
      : 0;
    const expect = dc.expectedRowCount !== undefined && dc.expectedRowCount !== null
      ? `rows=${dc.expectedRowCount}`
      : assertionCount > 0
        ? `assertions=${assertionCount}`
        : legacyCount > 0
          ? `cols=${legacyCount}`
          : '(no expectations)';
    const cap = captureCount > 0 ? ` / captures=${captureCount}` : '';
    return <>SQL / {expect}{cap}</>;
  }
  if (p.eventAssert) {
    const ea = p.eventAssert;
    const entity = ea.entity?.type === 'Topic'
      ? `topic ${ea.entity?.name ?? '?'}/${ea.entity?.subscriptionName ?? '?'}`
      : `queue ${ea.entity?.name ?? '?'}`;
    const matchMode = ea.matchMode ?? 'AnyMessage';
    let mode: string = matchMode;
    if (matchMode === 'ExactCount' || matchMode === 'MinCount' || matchMode === 'MaxCount') {
      mode = `${matchMode}(${ea.expectedCount ?? '?'})`;
    } else if (matchMode === 'CountRange') {
      mode = `CountRange(${ea.expectedCount ?? '?'}, ${ea.maxCount ?? '?'})`;
    }
    const criteriaCount = (ea.criteria ?? []).length;
    const capturesCount = (ea.captures ?? []).length;
    const drain = ea.drainBeforeParent ? ' · drain' : '';
    const cap = capturesCount > 0 ? ` / captures=${capturesCount}` : '';
    return <>{entity} · {mode} · criteria={criteriaCount}{cap}{drain}</>;
  }
  if (p.api) return <>{p.api.method} {p.api.endpoint}</>;
  if (p.aseXmlDeliver) return <>deliver {p.aseXmlDeliver.templateId} → {p.aseXmlDeliver.endpointCode}</>;
  if (p.aseXml) return <>render {p.aseXml.templateId}</>;
  return <em style={{ color: '#94a3b8' }}>(no payload)</em>;
}

function DbCheckBlock({ dc }: { dc: NonNullable<PostStep['dbCheck']> }) {
  const assertions = dc.columnAssertions ?? [];
  const captures = dc.captures ?? [];
  const legacy = dc.expectedColumnValues ?? {};

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
      {dc.expectedRowCount !== undefined && dc.expectedRowCount !== null && (
        <div style={{ color: '#64748b' }}>
          <strong>expected row count:</strong> {dc.expectedRowCount}
        </div>
      )}
      {assertions.length > 0 && (
        <div style={{ color: '#64748b', marginTop: 4 }}>
          <strong>assertions:</strong>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
            {assertions.map((a, i) => (
              <span key={i} style={pillStyle}>
                <strong style={{ color: '#4f46e5' }}>{a.column}</strong>
                {a.jsonPath ? <span style={{ color: '#7c3aed' }}>.{a.jsonPath}</span> : null}
                {' '}{a.operator}{' '}
                {a.operator !== 'IsNull' && a.operator !== 'IsNotNull' ? `'${a.expected}'` : ''}
                {a.operator === 'Between' && a.expected2 ? ` … '${a.expected2}'` : ''}
              </span>
            ))}
          </div>
        </div>
      )}
      {captures.length > 0 && (
        <div style={{ color: '#64748b', marginTop: 4 }}>
          <strong>captures:</strong>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
            {captures.map((c, i) => (
              <span key={i} style={pillStyle}>
                <strong style={{ color: '#0d9488' }}>{`{{${c.as}}}`}</strong>
                {' ← '}{c.column}{c.jsonPath ? `.${c.jsonPath}` : ''}
                {!c.required ? ' (optional)' : ''}
              </span>
            ))}
          </div>
        </div>
      )}
      {assertions.length === 0 && Object.keys(legacy).length > 0 && (
        <div style={{ color: '#92400e', marginTop: 4, fontStyle: 'italic' }}>
          (legacy expectedColumnValues — will normalise on next save)
        </div>
      )}
    </div>
  );
}

function EventAssertBlock({ ea }: { ea: NonNullable<PostStep['eventAssert']> }) {
  // Defensive — every field can be missing if a stale persisted shape or a
  // partial LLM emission lands here. A single malformed entry must not bring
  // down the whole panel.
  const entity = ea.entity;
  const entityLabel = entity?.type === 'Topic'
    ? `Topic ${entity.name ?? '?'} / Sub ${entity.subscriptionName ?? '?'}`
    : `Queue ${entity?.name ?? '?'}`;
  const matchMode = ea.matchMode ?? 'AnyMessage';
  let modeLabel: string = matchMode;
  if (matchMode === 'ExactCount' || matchMode === 'MinCount' || matchMode === 'MaxCount') {
    modeLabel = `${matchMode}(${ea.expectedCount ?? '?'})`;
  } else if (matchMode === 'CountRange') {
    modeLabel = `CountRange(${ea.expectedCount ?? '?'}, ${ea.maxCount ?? '?'})`;
  }
  const criteria = ea.criteria ?? [];
  const captures = ea.captures ?? [];

  return (
    <div style={{ marginTop: 6, fontSize: 12 }}>
      <div style={{ color: '#64748b', marginBottom: 4 }}>
        <strong>connection:</strong> <code style={inlineCode}>{ea.connectionKey ?? '?'}</code>
        <span style={{ marginLeft: 10 }}>
          <strong>entity:</strong> {entityLabel}
        </span>
        <span style={{ marginLeft: 10 }}>
          <strong>match:</strong> {modeLabel}
        </span>
      </div>
      <div style={{ color: '#64748b', marginBottom: 4 }}>
        <strong>timeout:</strong> {ea.timeoutSeconds ?? '?'}s
        <span style={{ marginLeft: 10 }}>
          <strong>max msgs:</strong> {ea.maxMessages ?? '?'}
        </span>
        {ea.bodyFormat && ea.bodyFormat !== 'Auto' && (
          <span style={{ marginLeft: 10 }}>
            <strong>body:</strong> {ea.bodyFormat}
          </span>
        )}
        {ea.receiveMode && ea.receiveMode !== 'PeekLock' && (
          <span style={{ marginLeft: 10 }}>
            <strong>receive:</strong> {ea.receiveMode}
          </span>
        )}
        {ea.drainBeforeParent && (
          <span style={{ marginLeft: 10, color: '#b45309' }}>
            <strong>drain before parent</strong>
          </span>
        )}
        {ea.completeOnPass === false && (
          <span style={{ marginLeft: 10, color: '#475569' }}>
            <strong>completeOnPass:</strong> false
          </span>
        )}
      </div>
      {(ea.correlationFilter || ea.sessionId) && (
        <div style={{ color: '#64748b', marginBottom: 4 }}>
          {ea.correlationFilter && (
            <span>
              <strong>correlationId:</strong> <code style={inlineCode}>{ea.correlationFilter}</code>
            </span>
          )}
          {ea.sessionId && (
            <span style={{ marginLeft: 10 }}>
              <strong>sessionId:</strong> <code style={inlineCode}>{ea.sessionId}</code>
            </span>
          )}
        </div>
      )}
      {criteria.length > 0 && (
        <div style={{ color: '#64748b', marginTop: 4 }}>
          <strong>criteria:</strong>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
            {criteria.map((c, i) => {
              if (!c) return null;
              const op = c.operator ?? 'Equals';
              return (
                <span key={i} style={pillStyle}>
                  <strong style={{ color: '#4f46e5' }}>{c.field ?? '?'}</strong>
                  {' '}{op}{' '}
                  {op !== 'IsNull' && op !== 'IsNotNull' ? `'${c.expected ?? ''}'` : ''}
                  {op === 'Between' && c.expected2 ? ` … '${c.expected2}'` : ''}
                </span>
              );
            })}
          </div>
        </div>
      )}
      {captures.length > 0 && (
        <div style={{ color: '#64748b', marginTop: 4 }}>
          <strong>captures:</strong>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 4 }}>
            {captures.map((c, i) => {
              if (!c) return null;
              return (
                <span key={i} style={pillStyle}>
                  <strong style={{ color: '#0d9488' }}>{`{{${c.as ?? '?'}}}`}</strong>
                  {' ← '}{c.field ?? '?'}
                  {c.required === false ? ' (optional)' : ''}
                </span>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

const pillStyle: React.CSSProperties = {
  fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 12,
  background: '#eef2ff', border: '1px solid #c7d2fe',
  borderRadius: 3, padding: '2px 6px',
};

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

function computeIsDeferred(
  postSteps: PostStep[],
  cfg: AseXmlVerificationConfigResponse | undefined,
): boolean {
  if (!cfg || !cfg.deferVerifications || postSteps.length === 0) return false;
  return postSteps.some(p => p.waitBeforeSeconds > cfg.verificationDeferThresholdSeconds);
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
