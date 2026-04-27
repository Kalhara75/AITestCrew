import type { TestObjective, AseXmlTestDefinition } from '../types';
import { PostStepsPanel } from './PostStepsPanel';

interface Props {
  objectives: TestObjective[];
  moduleId?: string;
  testSetId?: string;
  onTestCaseUpdated?: () => void;
}

/**
 * Read-only viewer for aseXML test cases.
 *
 * Phase 1 scope: no edit dialog — authoring is via LLM + rebaseline. The
 * planned edit dialog (Phase 1.5) will be driven by the template manifest's
 * field specs, letting users edit user-source fields directly.
 */
export function AseXmlTestCaseTable({ objectives, moduleId, testSetId, onTestCaseUpdated }: Props) {
  const allCases = objectives
    .filter(o => o.aseXmlSteps && o.aseXmlSteps.length > 0)
    .flatMap(o => o.aseXmlSteps.map((step, idx) => ({
      step,
      objectiveId: o.id,
      objectiveName: o.name,
      stepIndex: idx,
      key: `${o.id}-asexml-${idx}`,
    })));

  if (allCases.length === 0) {
    return <p style={{ color: '#94a3b8', fontSize: 14, padding: '8px 0' }}>No aseXML test cases.</p>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e2e8f0', textAlign: 'left' }}>
            <th style={thStyle}>Test Name</th>
            <th style={thStyle}>Transaction</th>
            <th style={thStyle}>Template</th>
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
                <FieldValuePreview step={tc.step} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {allCases.some(tc => (tc.step.postSteps?.length ?? 0) > 0) && (
        <div style={{ marginTop: 16 }}>
          {allCases.map(tc => (tc.step.postSteps?.length ?? 0) > 0 && (
            <PostStepsPanel
              key={`${tc.key}-poststeps`}
              parentKind="AseXml"
              parentIndex={tc.stepIndex}
              objectiveId={tc.objectiveId}
              caseName={tc.objectiveName}
              postSteps={tc.step.postSteps ?? []}
              moduleId={moduleId}
              testSetId={testSetId}
              onChanged={onTestCaseUpdated}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function FieldValuePreview({ step }: { step: AseXmlTestDefinition }) {
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
