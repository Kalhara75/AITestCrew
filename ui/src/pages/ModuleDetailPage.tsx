import { useState, useMemo, useEffect, useRef, useCallback } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchModule, fetchModuleTestSets, deleteModule } from '../api/modules';
import { TestSetCard } from '../components/TestSetCard';
import { CreateTestSetDialog } from '../components/CreateTestSetDialog';
import { RunObjectiveDialog } from '../components/RunObjectiveDialog';
import { ModuleRunBanner } from '../components/ModuleRunBanner';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { useActiveRun } from '../contexts/ActiveRunContext';

export function ModuleDetailPage() {
  const { moduleId } = useParams<{ moduleId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [showCreateTestSet, setShowCreateTestSet] = useState(false);
  const [showRunObjective, setShowRunObjective] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const { moduleRun, isModuleRunning, startModuleRun, error: runError } = useActiveRun();
  const isRunning = moduleId ? isModuleRunning(moduleId) : false;

  const handleDeleteModule = async () => {
    if (!moduleId) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await deleteModule(moduleId);
      queryClient.invalidateQueries({ queryKey: ['modules'] });
      navigate('/');
    } catch (err) {
      setDeleting(false);
      setShowDeleteConfirm(false);
      setDeleteError((err as Error).message || 'Failed to delete module');
    }
  };

  const { data: module, isLoading: loadingModule, error: moduleError } = useQuery({
    queryKey: ['module', moduleId],
    queryFn: () => fetchModule(moduleId!),
    enabled: !!moduleId,
  });

  const { data: testSets, isLoading: loadingTestSets, refetch } = useQuery({
    queryKey: ['moduleTestSets', moduleId],
    queryFn: () => fetchModuleTestSets(moduleId!),
    enabled: !!moduleId,
  });

  // Search, filter, sort state
  const [searchQuery, setSearchQuery] = useState('');
  const [sortBy, setSortBy] = useState<'name' | 'lastRun' | 'status'>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);

  // Progressive loading
  const PAGE_SIZE = 12;
  const [visibleCount, setVisibleCount] = useState(PAGE_SIZE);
  const sentinelRef = useRef<HTMLDivElement>(null);

  const filteredTestSets = useMemo(() => {
    if (!testSets) return [];
    let result = [...testSets];

    if (searchQuery.trim()) {
      const q = searchQuery.toLowerCase();
      result = result.filter(ts =>
        (ts.name || '').toLowerCase().includes(q) ||
        (ts.objective || '').toLowerCase().includes(q) ||
        (ts.id || '').toLowerCase().includes(q)
      );
    }

    if (statusFilter !== null) {
      if (statusFilter === 'No runs') {
        result = result.filter(ts => !ts.lastRunStatus);
      } else {
        result = result.filter(ts => ts.lastRunStatus === statusFilter);
      }
    }

    result.sort((a, b) => {
      let cmp = 0;
      if (sortBy === 'name') {
        cmp = (a.name || a.objective || a.id).localeCompare(b.name || b.objective || b.id);
      } else if (sortBy === 'lastRun') {
        const aTime = a.lastRunAt && a.lastRunAt !== '0001-01-01T00:00:00' ? new Date(a.lastRunAt).getTime() : 0;
        const bTime = b.lastRunAt && b.lastRunAt !== '0001-01-01T00:00:00' ? new Date(b.lastRunAt).getTime() : 0;
        cmp = aTime - bTime;
      } else if (sortBy === 'status') {
        const order: Record<string, number> = { Failed: 0, Error: 1, Running: 2, Passed: 3 };
        const aOrd = a.lastRunStatus ? (order[a.lastRunStatus] ?? 4) : 5;
        const bOrd = b.lastRunStatus ? (order[b.lastRunStatus] ?? 4) : 5;
        cmp = aOrd - bOrd;
      }
      return sortDir === 'desc' ? -cmp : cmp;
    });

    return result;
  }, [testSets, searchQuery, sortBy, sortDir, statusFilter]);

  // Reset visible count when filters change
  useEffect(() => {
    setVisibleCount(PAGE_SIZE);
  }, [searchQuery, sortBy, sortDir, statusFilter]);

  const visibleTestSets = filteredTestSets.slice(0, visibleCount);
  const hasMore = visibleCount < filteredTestSets.length;

  // IntersectionObserver for progressive loading
  const loadMore = useCallback(() => {
    setVisibleCount(prev => Math.min(prev + PAGE_SIZE, filteredTestSets.length));
  }, [filteredTestSets.length]);

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el || !hasMore) return;
    const observer = new IntersectionObserver(
      entries => { if (entries[0].isIntersecting) loadMore(); },
      { rootMargin: '200px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasMore, loadMore]);

  // Always show Passed and Failed chips; show others only when present in data
  const availableStatuses = useMemo(() => {
    if (!testSets) return [];
    const present = new Set<string>();
    for (const ts of testSets) {
      present.add(ts.lastRunStatus || 'No runs');
    }
    const alwaysShow = ['Passed', 'Failed'];
    return ['Passed', 'Failed', 'Error', 'Running', 'No runs'].filter(s => alwaysShow.includes(s) || present.has(s));
  }, [testSets]);

  const clearFilters = () => {
    setSearchQuery('');
    setStatusFilter(null);
    setSortBy('name');
    setSortDir('asc');
  };

  if (loadingModule || loadingTestSets) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Loading module...</p>;
  if (moduleError) return <p style={{ color: '#dc2626', padding: 40, textAlign: 'center' }}>Error: {(moduleError as Error).message}</p>;
  if (!module) return <p style={{ color: '#64748b', padding: 40, textAlign: 'center' }}>Module not found.</p>;

  return (
    <div>
      {/* Breadcrumb */}
      <div style={{ fontSize: 13, color: '#94a3b8', marginBottom: 20 }}>
        <Link to="/" style={{ color: '#2563eb', textDecoration: 'none' }}>Modules</Link>
        <span style={{ margin: '0 8px' }}>/</span>
        <span style={{ color: '#64748b' }}>{module.name}</span>
      </div>

      {/* Header */}
      <div style={cardStyle({ marginBottom: 24 })}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 16, flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 280 }}>
            <h1 style={{ margin: '0 0 8px', fontSize: 22, fontWeight: 700, color: '#0f172a' }}>
              {module.name}
            </h1>
            {module.description && (
              <p style={{ margin: '0 0 16px', fontSize: 14, color: '#64748b', lineHeight: 1.5 }}>
                {module.description}
              </p>
            )}
            <div style={{ display: 'flex', gap: 8 }}>
              <StatPill label="Test Sets" value={module.testSetCount} />
              <StatPill label="Objectives" value={module.totalObjectives} />
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => setShowCreateTestSet(true)} style={btnStyle('#2563eb')}>
              + Test Set
            </button>
            <button
              onClick={() => setShowRunObjective(true)}
              disabled={!testSets || testSets.length === 0 || isRunning}
              style={{
                ...btnStyle('#16a34a'),
                opacity: !testSets || testSets.length === 0 || isRunning ? 0.5 : 1,
                cursor: !testSets || testSets.length === 0 || isRunning ? 'not-allowed' : 'pointer',
              }}
            >
              Run Objective
            </button>
            <button
              onClick={() => moduleId && startModuleRun(moduleId)}
              disabled={!testSets || testSets.length === 0 || isRunning}
              style={{
                ...btnStyle('#7c3aed'),
                opacity: !testSets || testSets.length === 0 || isRunning ? 0.5 : 1,
                cursor: !testSets || testSets.length === 0 || isRunning ? 'not-allowed' : 'pointer',
              }}
            >
              Run All
            </button>
            <button
              onClick={() => { setDeleteError(null); setShowDeleteConfirm(true); }}
              disabled={isRunning}
              style={{
                ...btnStyle('#dc2626'),
                opacity: isRunning ? 0.5 : 1,
                cursor: isRunning ? 'not-allowed' : 'pointer',
              }}
            >
              Delete Module
            </button>
          </div>
        </div>
      </div>

      {/* Module run progress banner */}
      {isRunning && moduleRun && (
        <ModuleRunBanner moduleRun={moduleRun} moduleId={moduleId!} />
      )}

      {/* Module run error */}
      {runError && (
        <div style={{
          background: '#fef2f2', border: '1px solid #fecaca', borderRadius: 8,
          padding: '10px 16px', marginBottom: 16, fontSize: 13, color: '#dc2626',
        }}>
          {runError}
        </div>
      )}

      {/* Delete error */}
      {deleteError && (
        <div style={{
          background: '#fef2f2', border: '1px solid #fecaca', borderRadius: 8,
          padding: '10px 16px', marginBottom: 16, fontSize: 13, color: '#dc2626',
        }}>
          {deleteError}
        </div>
      )}

      {/* Test Sets Toolbar */}
      <div style={{ marginBottom: 16 }}>
        {/* Row 1: Heading + count */}
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
          <h2 style={{ margin: 0, fontSize: 17, fontWeight: 600, color: '#0f172a' }}>Test Sets</h2>
          <span style={{
            fontSize: 12, fontWeight: 600, color: '#64748b', background: '#f1f5f9',
            padding: '2px 10px', borderRadius: 12,
          }}>{testSets?.length || 0}</span>
        </div>

        {/* Row 2: Search, sort, status filters — only show when there are test sets */}
        {testSets && testSets.length > 0 && (
          <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
            {/* Search */}
            <input
              type="text"
              placeholder="Search test sets..."
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              style={searchInputStyle}
            />

            {/* Sort */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
              <select
                value={sortBy}
                onChange={e => setSortBy(e.target.value as 'name' | 'lastRun' | 'status')}
                style={selectStyle}
              >
                <option value="name">Name</option>
                <option value="lastRun">Last Run</option>
                <option value="status">Status</option>
              </select>
              <button
                onClick={() => setSortDir(d => d === 'asc' ? 'desc' : 'asc')}
                style={sortDirBtnStyle}
                title={sortDir === 'asc' ? 'Ascending' : 'Descending'}
              >
                {sortDir === 'asc' ? '\u2191' : '\u2193'}
              </button>
            </div>

            {/* Status filter chips */}
            <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
              <button
                onClick={() => setStatusFilter(null)}
                style={chipStyle(statusFilter === null)}
              >
                All
              </button>
              {availableStatuses.map(s => (
                <button
                  key={s}
                  onClick={() => setStatusFilter(statusFilter === s ? null : s)}
                  style={chipStyle(statusFilter === s, s)}
                >
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}
      </div>

      {/* Test Sets Grid */}
      {!testSets || testSets.length === 0 ? (
        <div style={{
          background: '#fff', borderRadius: 10, padding: 48, textAlign: 'center',
          border: '1px solid #e2e8f0',
        }}>
          <div style={{ fontSize: 40, marginBottom: 16 }}>{'\u{1F9EA}'}</div>
          <p style={{ color: '#475569', fontSize: 16, margin: '0 0 8px', fontWeight: 500 }}>
            No test sets in this module
          </p>
          <p style={{ color: '#94a3b8', fontSize: 14, margin: '0 0 20px' }}>
            Create a test set, then run objectives against it.
          </p>
          <button onClick={() => setShowCreateTestSet(true)} style={btnStyle('#2563eb')}>
            + Create Test Set
          </button>
        </div>
      ) : filteredTestSets.length === 0 ? (
        <div style={{
          background: '#fff', borderRadius: 10, padding: 48, textAlign: 'center',
          border: '1px solid #e2e8f0',
        }}>
          <p style={{ color: '#475569', fontSize: 16, margin: '0 0 8px', fontWeight: 500 }}>
            No test sets match your filters
          </p>
          <p style={{ color: '#94a3b8', fontSize: 14, margin: '0 0 20px' }}>
            Try adjusting your search or filter criteria.
          </p>
          <button onClick={clearFilters} style={btnStyle('#64748b')}>
            Clear Filters
          </button>
        </div>
      ) : (
        <>
          <div style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(340px, 1fr))',
            gap: 20,
          }}>
            {visibleTestSets.map(ts => (
              <TestSetCard
                key={ts.id}
                ts={ts}
                moduleId={moduleId!}
                isRunning={isRunning && (moduleRun?.currentTestSetIds?.includes(ts.id) ?? false)}
              />
            ))}
          </div>

          {/* Progressive loading sentinel + indicator */}
          {hasMore && (
            <div
              ref={sentinelRef}
              style={{ textAlign: 'center', padding: '24px 0 8px', color: '#94a3b8', fontSize: 13 }}
            >
              Showing {visibleCount} of {filteredTestSets.length} test sets
              <span style={{ marginLeft: 8, display: 'inline-block', animation: 'pulse 1.5s infinite' }}>...</span>
            </div>
          )}
          {!hasMore && filteredTestSets.length > PAGE_SIZE && (
            <div style={{ textAlign: 'center', padding: '16px 0 0', color: '#94a3b8', fontSize: 12 }}>
              All {filteredTestSets.length} test sets shown
            </div>
          )}
        </>
      )}

      <CreateTestSetDialog
        open={showCreateTestSet}
        moduleId={moduleId!}
        onClose={() => setShowCreateTestSet(false)}
        onCreated={() => refetch()}
      />

      {testSets && testSets.length > 0 && (
        <RunObjectiveDialog
          open={showRunObjective}
          moduleId={moduleId!}
          testSets={testSets}
          onClose={() => setShowRunObjective(false)}
        />
      )}

      <ConfirmDialog
        open={showDeleteConfirm}
        title="Delete Module"
        message={`This will permanently delete "${module.name}" and all ${module.testSetCount} test set${module.testSetCount === 1 ? '' : 's'} along with their execution history. This action cannot be undone.`}
        confirmLabel="Delete"
        confirmDestructive
        loading={deleting}
        onConfirm={handleDeleteModule}
        onCancel={() => setShowDeleteConfirm(false)}
      />
    </div>
  );
}

function StatPill({ label, value }: { label: string; value: string | number }) {
  return (
    <span style={{
      fontSize: 13, color: '#475569', background: '#f8fafc',
      padding: '4px 12px', borderRadius: 6, border: '1px solid #f1f5f9',
    }}>
      <span style={{ color: '#94a3b8', marginRight: 4 }}>{label}:</span>
      <span style={{ fontWeight: 600 }}>{value}</span>
    </span>
  );
}

function cardStyle(extra: React.CSSProperties): React.CSSProperties {
  return { background: '#fff', borderRadius: 10, border: '1px solid #e2e8f0', padding: 24, ...extra };
}

const btnStyle = (bg: string): React.CSSProperties => ({
  background: bg, color: '#fff', border: 'none', padding: '8px 18px',
  borderRadius: 8, fontSize: 13, fontWeight: 600, cursor: 'pointer',
});

const searchInputStyle: React.CSSProperties = {
  flex: '1 1 180px', minWidth: 140, maxWidth: 300,
  background: '#fff', border: '1px solid #e2e8f0', borderRadius: 8,
  padding: '6px 12px', fontSize: 13, color: '#0f172a', outline: 'none',
};

const selectStyle: React.CSSProperties = {
  background: '#fff', border: '1px solid #e2e8f0', borderRadius: 8,
  padding: '6px 10px', fontSize: 13, color: '#0f172a', cursor: 'pointer', outline: 'none',
};

const sortDirBtnStyle: React.CSSProperties = {
  background: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: 6,
  padding: '4px 8px', fontSize: 14, cursor: 'pointer', lineHeight: 1, color: '#475569',
};

const statusColors: Record<string, { bg: string; fg: string; border: string }> = {
  Passed:  { bg: '#dcfce7', fg: '#166534', border: '#bbf7d0' },
  Failed:  { bg: '#fee2e2', fg: '#991b1b', border: '#fecaca' },
  Error:   { bg: '#fef3c7', fg: '#92400e', border: '#fde68a' },
  Running: { bg: '#dbeafe', fg: '#1e40af', border: '#bfdbfe' },
  'No runs': { bg: '#f1f5f9', fg: '#475569', border: '#e2e8f0' },
};

function chipStyle(active: boolean, status?: string): React.CSSProperties {
  const c = status ? statusColors[status] : undefined;
  if (active && c) {
    return {
      background: c.bg, color: c.fg, border: `2px solid ${c.fg}`,
      padding: '3px 12px', borderRadius: 14, fontSize: 12, fontWeight: 600,
      cursor: 'pointer', whiteSpace: 'nowrap',
    };
  }
  if (active) {
    return {
      background: '#0f172a', color: '#fff', border: '2px solid #0f172a',
      padding: '3px 12px', borderRadius: 14, fontSize: 12, fontWeight: 600,
      cursor: 'pointer', whiteSpace: 'nowrap',
    };
  }
  if (c) {
    return {
      background: c.bg, color: c.fg, border: `1px solid ${c.border}`,
      padding: '4px 12px', borderRadius: 14, fontSize: 12, fontWeight: 600,
      cursor: 'pointer', whiteSpace: 'nowrap',
    };
  }
  return {
    background: '#f8fafc', color: '#64748b', border: '1px solid #e2e8f0',
    padding: '4px 12px', borderRadius: 14, fontSize: 12, fontWeight: 600,
    cursor: 'pointer', whiteSpace: 'nowrap',
  };
}
