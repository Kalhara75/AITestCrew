import { useState } from 'react';
import {
  previewXrayImport,
  confirmXrayImport,
  type XrayImportPreview,
  type XrayImportResult,
  type ProposedObjective,
} from '../api/xray';

interface Props {
  open: boolean;
  moduleId: string;
  testSetId: string;
  onClose: () => void;
  onImported: (result: XrayImportResult) => void;
}

type Phase = 'input' | 'loading' | 'review' | 'confirming' | 'done';

interface MergeRequest {
  slugToMerge: string;
  mergeIntoSlug: string;
}

function initAcceptedSlugs(objectives: ProposedObjective[]): Set<string> {
  return new Set(objectives.map(o => o.slug));
}

export function ImportFromXrayDialog({ open, moduleId, testSetId, onClose, onImported }: Props) {
  const [phase, setPhase] = useState<Phase>('input');
  const [ticketKey, setTicketKey] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<XrayImportPreview | null>(null);
  const [result, setResult] = useState<XrayImportResult | null>(null);
  const [collapseToSingle, setCollapseToSingle] = useState(false);

  // Per-card state -- all reset when a new Preview is fetched
  const [acceptedSlugs, setAcceptedSlugs] = useState<Set<string>>(new Set());
  const [titleOverrides, setTitleOverrides] = useState<Record<string, string>>({});
  const [mergeRequests, setMergeRequests] = useState<MergeRequest[]>([]);

  if (!open) return null;

  const handlePreview = async () => {
    if (!ticketKey.trim()) { setError('Ticket key is required.'); return; }
    setError(null);
    setPhase('loading');
    try {
      const p = await previewXrayImport({ ticketKey: ticketKey.trim(), moduleId, testSetId });
      setPreview(p);
      setAcceptedSlugs(initAcceptedSlugs(p.proposedObjectives));
      setTitleOverrides({});
      setMergeRequests([]);
      setCollapseToSingle(false);
      setPhase('review');
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Import preview failed');
      setPhase('input');
    }
  };

  const computeAcceptedObjectiveSlugs = () => {
    if (!preview) return [];
    const allSlugs = preview.proposedObjectives.map(o => o.slug);
    const mergedSlugs = new Set(mergeRequests.map(r => r.slugToMerge));
    const hasTouched =
      allSlugs.some(s => !acceptedSlugs.has(s)) ||
      mergeRequests.length > 0 ||
      Object.keys(titleOverrides).length > 0;
    if (!hasTouched) return [];
    return allSlugs.filter(s => acceptedSlugs.has(s) && !mergedSlugs.has(s));
  };

  const handleConfirm = async () => {
    if (!preview) return;
    setPhase('confirming');
    try {
      const req = {
        preview,
        acceptedObjectiveSlugs: collapseToSingle ? [] : computeAcceptedObjectiveSlugs(),
        collapseToSingle,
        titleOverrides: collapseToSingle ? {} : titleOverrides,
        mergeRequests: collapseToSingle ? [] : mergeRequests,
      };
      const r = await confirmXrayImport(req);
      setResult(r);
      setPhase('done');
      onImported(r);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Confirm failed');
      setPhase('review');
    }
  };

  const handleToggleAccepted = (slug: string, checked: boolean) => {
    setAcceptedSlugs(prev => {
      const next = new Set(prev);
      if (checked) next.add(slug); else next.delete(slug);
      return next;
    });
  };

  const handleTitleChange = (slug: string, originalTitle: string, newTitle: string) => {
    setTitleOverrides(prev => {
      const next = { ...prev };
      if (newTitle !== originalTitle) {
        next[slug] = newTitle;
      } else {
        delete next[slug];
      }
      return next;
    });
  };

  const handleMergeIntoAbove = (slug: string, prevSlug: string) => {
    setMergeRequests(prev => [...prev, { slugToMerge: slug, mergeIntoSlug: prevSlug }]);
    setAcceptedSlugs(prev => {
      const next = new Set(prev);
      next.delete(slug);
      return next;
    });
  };

  const handleUndoMerge = (slug: string) => {
    setMergeRequests(prev => prev.filter(r => r.slugToMerge !== slug));
    setAcceptedSlugs(prev => {
      const next = new Set(prev);
      next.add(slug);
      return next;
    });
  };

  const getImportLabel = () => {
    if (!preview) return 'Import';
    if (collapseToSingle) return 'Import (1 objective — collapsed)';
    const mergedSlugs = new Set(mergeRequests.map(r => r.slugToMerge));
    const count = preview.proposedObjectives.filter(
      o => acceptedSlugs.has(o.slug) && !mergedSlugs.has(o.slug)
    ).length;
    return `Import (${count} objective${count === 1 ? '' : 's'})`;
  };

  const findPrevAcceptedSlug = (objectives: ProposedObjective[], currentIndex: number): string | null => {
    const mergedSlugs = new Set(mergeRequests.map(r => r.slugToMerge));
    for (let i = currentIndex - 1; i >= 0; i--) {
      const s = objectives[i].slug;
      if (acceptedSlugs.has(s) && !mergedSlugs.has(s)) return s;
    }
    return null;
  };

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={dialogStyle} onClick={e => e.stopPropagation()}>
        <h2 style={{ margin: '0 0 20px', fontSize: 18, fontWeight: 700, color: '#0f172a' }}>
          Import from Jira Xray
        </h2>

        {error && <div style={errorStyle}>{error}</div>}

        {phase === 'input' && (
          <div>
            <label style={labelStyle}>Xray Ticket Key</label>
            <input
              style={inputStyle}
              placeholder='e.g. PROJ-1234'
              value={ticketKey}
              onChange={e => setTicketKey(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') handlePreview(); }}
              autoFocus
            />
            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
              <button type='button' onClick={onClose} style={cancelBtnStyle}>Cancel</button>
              <button type='button' onClick={handlePreview} style={primaryBtnStyle} disabled={!ticketKey.trim()}>
                Preview Import
              </button>
            </div>
          </div>
        )}

        {phase === 'loading' && (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '32px 0', color: '#64748b' }}>
            <Spinner />
            <p style={{ margin: '14px 0 0', fontSize: 13 }}>Fetching and mapping ticket from Jira Xray...</p>
          </div>
        )}

        {phase === 'review' && preview && (
          <div>
            <div style={{ fontSize: 13, color: '#475569', marginBottom: 14 }}>
              <span style={{ fontWeight: 700, color: '#0f172a' }}>{preview.ticketKey}</span>
              {preview.ticketSummary && <span style={{ marginLeft: 8 }}>{preview.ticketSummary}</span>}
            </div>

            {preview.reviewCarefullyFlag && (
              <div style={warningStyle}>
                More than 4 objectives proposed — please review carefully.
              </div>
            )}

            <div style={objectiveListStyle}>
              {preview.proposedObjectives.map((obj, i) => {
                const mergedEntry = mergeRequests.find(r => r.slugToMerge === obj.slug);
                const isMerged = !!mergedEntry;
                const isChecked = acceptedSlugs.has(obj.slug) && !isMerged;
                const currentTitle = titleOverrides[obj.slug] ?? obj.title;
                const prevAcceptedSlug = findPrevAcceptedSlug(preview.proposedObjectives, i);

                return (
                  <div
                    key={obj.slug}
                    style={{
                      padding: '10px 12px',
                      borderBottom: i < preview.proposedObjectives.length - 1 ? '1px solid #e2e8f0' : 'none',
                      opacity: isMerged ? 0.4 : (!isChecked ? 0.5 : 1),
                      background: isMerged ? '#f1f5f9' : undefined,
                    }}
                  >
                    {isMerged ? (
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <span style={{ fontSize: 12, color: '#64748b', fontStyle: 'italic' }}>
                          Merged into: {preview.proposedObjectives.find(o => o.slug === mergedEntry.mergeIntoSlug)?.title ?? mergedEntry.mergeIntoSlug}
                        </span>
                        <button
                          type='button'
                          disabled={collapseToSingle}
                          onClick={() => handleUndoMerge(obj.slug)}
                          style={undoMergeLinkStyle(collapseToSingle)}
                        >
                          Undo merge
                        </button>
                      </div>
                    ) : (
                      <>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                          <input
                            type='checkbox'
                            checked={isChecked}
                            disabled={collapseToSingle}
                            onChange={e => handleToggleAccepted(obj.slug, e.target.checked)}
                            style={{ flexShrink: 0, cursor: collapseToSingle ? 'not-allowed' : 'pointer' }}
                          />
                          <input
                            type='text'
                            value={currentTitle}
                            disabled={collapseToSingle}
                            onChange={e => handleTitleChange(obj.slug, obj.title, e.target.value)}
                            onBlur={e => handleTitleChange(obj.slug, obj.title, e.target.value)}
                            style={{
                              ...titleInputStyle,
                              opacity: collapseToSingle ? 0.5 : 1,
                              cursor: collapseToSingle ? 'not-allowed' : 'text',
                            }}
                            aria-label={`Title for objective ${i + 1}`}
                          />
                        </div>

                        {obj.rationale && (
                          <div style={{ fontSize: 12, color: '#64748b', marginTop: 3, paddingLeft: 24 }}>{obj.rationale}</div>
                        )}

                        {obj.mappingRows.some(r => r.kind === 'postStep') && (
                          <div style={{ paddingLeft: 24, marginTop: 4 }}>
                            {obj.mappingRows.map((row, rowIdx) => {
                              if (row.kind === 'postStep') {
                                const parentRow = row.parentFragmentIndex != null ? obj.mappingRows[row.parentFragmentIndex] : null;
                                return (
                                  <div key={rowIdx} style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2 }}>
                                    <span style={{ fontSize: 10, color: '#94a3b8' }}>{'└─'}</span>
                                    <span style={{ fontSize: 11, color: '#475569', flex: 1 }} title={row.sourceFragment}>
                                      {row.sourceFragment.length > 60 ? row.sourceFragment.slice(0, 60) + '…' : row.sourceFragment}
                                    </span>
                                    <span style={postStepKindBadgeStyle(row.postStepType ?? 'ui')}>{row.postStepType ?? 'ui'}</span>
                                    {parentRow && (
                                      <span style={{ fontSize: 10, color: '#94a3b8' }}>→ {parentRow.kind}</span>
                                    )}
                                  </div>
                                );
                              }
                              return null;
                            })}
                          </div>
                        )}

                        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 4, paddingLeft: 24 }}>
                          <div style={{ fontSize: 11, color: '#94a3b8', flex: 1 }}>
                            {obj.mappingRows.filter(r => r.kind !== 'postStep').length} parent step{obj.mappingRows.filter(r => r.kind !== 'postStep').length === 1 ? '' : 's'}
                            {obj.mappingRows.some(r => r.kind === 'postStep') && (
                              <span style={{ color: '#64748b' }}>, {obj.mappingRows.filter(r => r.kind === 'postStep').length} post-step{obj.mappingRows.filter(r => r.kind === 'postStep').length === 1 ? '' : 's'}</span>
                            )}
                            {' '}{[...new Set(obj.mappingRows.filter(r => r.kind !== 'postStep').map(r => r.kind))].join(', ')}
                          </div>
                          {i > 0 && (
                            <button
                              type='button'
                              disabled={collapseToSingle || !prevAcceptedSlug}
                              onClick={() => prevAcceptedSlug && handleMergeIntoAbove(obj.slug, prevAcceptedSlug)}
                              title={!prevAcceptedSlug ? 'No earlier objective to merge into' : 'Merge this objective into the one above'}
                              style={mergeButtonStyle(collapseToSingle || !prevAcceptedSlug)}
                            >
                              Merge into above
                            </button>
                          )}
                        </div>
                      </>
                    )}
                  </div>
                );
              })}
            </div>

            {preview.draftGapReqTitles.length > 0 && (
              <div style={gapReqStyle}>
                <div style={{ fontWeight: 600, marginBottom: 6 }}>
                  {preview.draftGapReqTitles.length} capability gap{preview.draftGapReqTitles.length === 1 ? '' : 's'} — stub REQ file{preview.draftGapReqTitles.length === 1 ? '' : 's'} will be written:
                </div>
                <ul style={{ margin: 0, paddingLeft: 18 }}>
                  {preview.draftGapReqTitles.map((t, idx) => <li key={idx}>{t}</li>)}
                </ul>
              </div>
            )}

            <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 14, fontSize: 13, color: '#475569', cursor: 'pointer' }}>
              <input
                type='checkbox'
                checked={collapseToSingle}
                onChange={e => setCollapseToSingle(e.target.checked)}
              />
              Collapse all objectives into one
            </label>

            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
              <button type='button' onClick={() => setPhase('input')} style={cancelBtnStyle}>Back</button>
              <button type='button' onClick={onClose} style={cancelBtnStyle}>Cancel</button>
              <button type='button' onClick={handleConfirm} style={primaryBtnStyle}>
                {getImportLabel()}
              </button>
            </div>
          </div>
        )}

        {phase === 'confirming' && (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '32px 0', color: '#64748b' }}>
            <Spinner />
            <p style={{ margin: '14px 0 0', fontSize: 13 }}>Persisting objectives and writing gap REQ files...</p>
          </div>
        )}

        {phase === 'done' && result && (
          <div>
            <div style={successStyle}>
              Import complete. {result.persistedObjectiveIds.length} objective{result.persistedObjectiveIds.length === 1 ? '' : 's'} added.
            </div>

            {result.placeholderStepDescriptions.length > 0 && (
              <div style={{ fontSize: 13, color: '#475569', marginTop: 12 }}>
                {result.placeholderStepDescriptions.length} step{result.placeholderStepDescriptions.length === 1 ? '' : 's'} imported as placeholder{result.placeholderStepDescriptions.length === 1 ? '' : 's'} — record or fill in manually.
              </div>
            )}

            {result.gapReqPaths.length > 0 && (
              <div style={gapReqStyle}>
                <div style={{ fontWeight: 600, marginBottom: 6 }}>
                  {result.gapReqPaths.length} gap REQ stub{result.gapReqPaths.length === 1 ? '' : 's'} written:
                </div>
                <ul style={{ margin: 0, paddingLeft: 18, fontFamily: 'ui-monospace,Consolas,monospace', fontSize: 11, wordBreak: 'break-all' }}>
                  {result.gapReqPaths.map((p, idx) => <li key={idx}>{p}</li>)}
                </ul>
              </div>
            )}

            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
              <button type='button' onClick={onClose} style={primaryBtnStyle}>Close</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function Spinner() {
  return (
    <svg
      width={32}
      height={32}
      viewBox='0 0 24 24'
      fill='none'
      style={{ animation: 'aitc-spin 0.9s linear infinite', color: '#2563eb' }}
      aria-hidden='true'
    >
      <style>{'@keyframes aitc-spin { to { transform: rotate(360deg); } }'}</style>
      <circle cx='12' cy='12' r='10' stroke='currentColor' strokeOpacity='0.25' strokeWidth='4' />
      <path d='M4 12a8 8 0 018-8' stroke='currentColor' strokeWidth='4' strokeLinecap='round' />
    </svg>
  );
}

const overlayStyle: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex',
  alignItems: 'center', justifyContent: 'center', zIndex: 1000,
};
const dialogStyle: React.CSSProperties = {
  background: '#fff', borderRadius: 12, padding: 28, width: 640, maxWidth: '90vw',
  maxHeight: '90vh', overflowY: 'auto', boxShadow: '0 20px 60px rgba(0,0,0,0.15)',
};
const labelStyle: React.CSSProperties = {
  display: 'block', fontSize: 13, fontWeight: 600, color: '#475569', marginBottom: 6,
};
const inputStyle: React.CSSProperties = {
  width: '100%', padding: '8px 12px', fontSize: 14, border: '1px solid #e2e8f0',
  borderRadius: 8, outline: 'none', boxSizing: 'border-box', fontFamily: 'inherit',
};
const titleInputStyle: React.CSSProperties = {
  flex: 1, padding: '3px 8px', fontSize: 13, fontWeight: 600, color: '#0f172a',
  border: '1px solid #e2e8f0', borderRadius: 6, outline: 'none',
  boxSizing: 'border-box', fontFamily: 'inherit', background: '#fff',
};
const cancelBtnStyle: React.CSSProperties = {
  background: '#f1f5f9', color: '#475569', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const primaryBtnStyle: React.CSSProperties = {
  background: '#2563eb', color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
};
const errorStyle: React.CSSProperties = {
  color: '#dc2626', fontSize: 13, marginBottom: 16, padding: '8px 12px',
  background: '#fef2f2', borderRadius: 6, border: '1px solid #fecaca',
};
const warningStyle: React.CSSProperties = {
  color: '#92400e', fontSize: 13, marginBottom: 12, padding: '8px 12px',
  background: '#fef3c7', borderRadius: 6, border: '1px solid #fde68a',
};
const successStyle: React.CSSProperties = {
  color: '#166534', fontSize: 14, fontWeight: 600, padding: '10px 14px',
  background: '#f0fdf4', borderRadius: 6, border: '1px solid #bbf7d0',
};
const objectiveListStyle: React.CSSProperties = {
  border: '1px solid #e2e8f0', borderRadius: 8, maxHeight: 320, overflowY: 'auto',
  background: '#f8fafc',
};
const gapReqStyle: React.CSSProperties = {
  marginTop: 14, padding: '10px 12px', fontSize: 12, color: '#9a3412',
  background: '#fff7ed', borderRadius: 6, border: '1px solid #fed7aa',
};

function mergeButtonStyle(disabled: boolean): React.CSSProperties {
  return {
    background: '#f1f5f9', color: disabled ? '#94a3b8' : '#475569',
    border: '1px solid #e2e8f0', padding: '2px 8px', borderRadius: 6,
    fontSize: 11, fontWeight: 600, cursor: disabled ? 'not-allowed' : 'pointer',
    opacity: disabled ? 0.5 : 1,
  };
}

function undoMergeLinkStyle(disabled: boolean): React.CSSProperties {
  return {
    background: 'none', border: 'none', color: '#2563eb',
    cursor: disabled ? 'not-allowed' : 'pointer',
    fontSize: 11, textDecoration: 'underline', padding: 0,
    opacity: disabled ? 0.5 : 1,
  };
}

function postStepKindBadgeStyle(kind: string): React.CSSProperties {
  const colorMap: Record<string, string> = {
    dbAssert: '#0ea5e9',
    eventAssert: '#8b5cf6',
    apiPostStep: '#10b981',
    uiVerification: '#f59e0b',
  };
  return {
    fontSize: 10, fontWeight: 600, padding: '1px 5px',
    borderRadius: 4, color: '#fff',
    background: colorMap[kind] ?? '#64748b',
    flexShrink: 0,
  };
}
