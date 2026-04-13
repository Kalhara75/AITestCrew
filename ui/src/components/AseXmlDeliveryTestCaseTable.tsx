import type { TestObjective, AseXmlDeliveryTestDefinition } from '../types';

interface Props {
  objectives: TestObjective[];
}

/**
 * Read-only viewer for aseXML DELIVERY test cases — adds an Endpoint column
 * on top of the generation viewer. Phase 2 scope: no edit dialog.
 */
export function AseXmlDeliveryTestCaseTable({ objectives }: Props) {
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
    </div>
  );
}

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
