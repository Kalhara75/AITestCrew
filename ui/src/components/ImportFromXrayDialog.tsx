import { useState } from 'react';
import {
  previewXrayImport,
  confirmXrayImport,
  type XrayImportPreview,
  type XrayImportResult,
} from '../api/xray';

interface Props {
  open: boolean;
  moduleId: string;
  testSetId: string;
  onClose: () => void;
  onImported: (result: XrayImportResult) => void;
}

type Phase = 'input' | 'loading' | 'review' | 'confirming' | 'done';

export function ImportFromXrayDialog({ open, moduleId, testSetId, onClose, onImported }: Props) {
  const [phase, setPhase] = useState<Phase>('input');
  const [ticketKey, setTicketKey] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<XrayImportPreview | null>(null);
  const [result, setResult] = useState<XrayImportResult | null>(null);
  const [acceptAll] = useState(true);
  const [collapseToSingle, setCollapseToSingle] = useState(false);

  if (!open) return null;

  const handlePreview = async () => {
    if (!ticketKey.trim()) { setError('Ticket key is required.'); return; }
    setError(null);
    setPhase('loading');
    try {
      const p = await previewXrayImport({ ticketKey: ticketKey.trim(), moduleId, testSetId });
      setPreview(p);
      setPhase('review');
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Import preview failed');
      setPhase('input');
    }
  };

  const handleConfirm = async () => {
    if (!preview) return;
    setPhase('confirming');
    try {
      const req = {
        preview,
        acceptedObjectiveSlugs: acceptAll ? [] : preview.proposedObjectives.map(o => o.slug),
        collapseToSingle,
        titleOverrides: {},
        mergeRequests: [],
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

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-2xl max-h-[90vh] overflow-y-auto">
        <h2 className="text-lg font-bold mb-4">Import from Jira Xray</h2>

        {error && (
          <div className="mb-4 rounded bg-red-50 border border-red-300 px-3 py-2 text-red-700 text-sm">
            {error}
          </div>
        )}

        {phase === 'input' && (
          <div className="space-y-4">
            <label className="block text-sm font-medium text-gray-700">
              Xray Ticket Key
              <input
                className="mt-1 block w-full rounded border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                placeholder="e.g. PROJ-1234"
                value={ticketKey}
                onChange={e => setTicketKey(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') handlePreview(); }}
              />
            </label>
            <div className="flex gap-2">
              <button
                className="px-4 py-2 bg-blue-600 text-white rounded text-sm hover:bg-blue-700 disabled:opacity-50"
                onClick={handlePreview}
              >
                Preview Import
              </button>
              <button className="px-4 py-2 border rounded text-sm hover:bg-gray-50" onClick={onClose}>Cancel</button>
            </div>
          </div>
        )}

        {phase === 'loading' && (
          <div className="flex flex-col items-center py-8 text-gray-500">
            <svg className="animate-spin h-8 w-8 mb-3 text-blue-600" viewBox="0 0 24 24" fill="none">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.4 0 0 5.4 0 12h4z" />
            </svg>
            <p>Fetching and mapping ticket from Jira Xray...</p>
          </div>
        )}

        {phase === 'review' && preview && (
          <div className="space-y-4">
            <div className="text-sm text-gray-600">
              <span className="font-medium">{preview.ticketKey}</span>{' '}
              {preview.ticketSummary}
            </div>
            {preview.reviewCarefullyFlag && (
              <div className="rounded bg-yellow-50 border border-yellow-300 px-3 py-2 text-yellow-800 text-sm">
                More than 4 objectives proposed -- please review carefully.
              </div>
            )}
            <div className="space-y-3 max-h-64 overflow-y-auto border rounded p-3">
              {preview.proposedObjectives.map((obj) => (
                <div key={obj.slug} className="border-b pb-2 last:border-b-0">
                  <p className="font-medium text-sm">{obj.title}</p>
                  <p className="text-xs text-gray-500">{obj.rationale}</p>
                  <p className="text-xs text-gray-400 mt-1">
                    {obj.mappingRows.length} step(s): {' '}
                    {[...new Set(obj.mappingRows.map(r => r.kind))].join(', ')}
                  </p>
                </div>
              ))}
            </div>
            {preview.draftGapReqTitles.length > 0 && (
              <div className="rounded bg-orange-50 border border-orange-200 px-3 py-2 text-sm">
                <p className="font-medium text-orange-800 mb-1">
                  {preview.draftGapReqTitles.length} capability gap(s) will create stub REQ files:
                </p>
                <ul className="list-disc ml-4 text-orange-700">
                  {preview.draftGapReqTitles.map((t, i) => <li key={i}>{t}</li>)}
                </ul>
              </div>
            )}
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={collapseToSingle} onChange={e => setCollapseToSingle(e.target.checked)} />
              Collapse all objectives into one
            </label>
            <div className="flex gap-2">
              <button className="px-4 py-2 bg-blue-600 text-white rounded text-sm hover:bg-blue-700" onClick={handleConfirm}>
                Import ({preview.proposedObjectives.length} objective{preview.proposedObjectives.length !== 1 ? 's' : ''})
              </button>
              <button className="px-4 py-2 border rounded text-sm hover:bg-gray-50" onClick={() => setPhase('input')}>Back</button>
              <button className="px-4 py-2 border rounded text-sm hover:bg-gray-50" onClick={onClose}>Cancel</button>
            </div>
          </div>
        )}

        {phase === 'confirming' && (
          <div className="flex flex-col items-center py-8 text-gray-500">
            <p>Persisting objectives and writing gap REQ files...</p>
          </div>
        )}

        {phase === 'done' && result && (
          <div className="space-y-3">
            <p className="text-green-700 font-medium">
              Import complete. {result.persistedObjectiveIds.length} objective(s) added.
            </p>
            {result.placeholderStepDescriptions.length > 0 && (
              <p className="text-sm text-gray-600">
                {result.placeholderStepDescriptions.length} step(s) imported as placeholders -- record or fill in manually.
              </p>
            )}
            {result.gapReqPaths.length > 0 && (
              <div className="text-sm text-orange-700">
                <p className="font-medium">{result.gapReqPaths.length} gap REQ stub(s) written:</p>
                <ul className="list-disc ml-4 text-xs break-all">
                  {result.gapReqPaths.map((p, i) => <li key={i}>{p}</li>)}
                </ul>
              </div>
            )}
            <button className="px-4 py-2 bg-blue-600 text-white rounded text-sm hover:bg-blue-700" onClick={onClose}>Close</button>
          </div>
        )}

      </div>
    </div>
  );
}

