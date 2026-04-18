import { createContext, useContext, useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchActiveRun, fetchRunStatus } from '../api/runs';
import { triggerModuleRun, fetchModuleRunStatus } from '../api/modules';
import type { ModuleRunStatus, RunStatusResponse } from '../types';

/** Tracks an individual (test-set or objective) run. */
export interface IndividualRun {
  runId: string;
  testSetId: string;
  moduleId?: string;
  objectiveId?: string;
}

interface ActiveRunContextValue {
  /* ── module-level run ── */
  moduleRun: ModuleRunStatus | null;
  isModuleRunning: (moduleId: string) => boolean;
  startModuleRun: (moduleId: string) => Promise<void>;

  /* ── individual run ── */
  individualRun: IndividualRun | null;
  individualRunStatus: RunStatusResponse | null;
  setIndividualRun: (run: IndividualRun | null) => void;
  isTestSetRunning: (testSetId: string) => boolean;

  error: string | null;
}

const ActiveRunContext = createContext<ActiveRunContextValue>({
  moduleRun: null,
  isModuleRunning: () => false,
  startModuleRun: async () => {},
  individualRun: null,
  individualRunStatus: null,
  setIndividualRun: () => {},
  isTestSetRunning: () => false,
  error: null,
});

export function useActiveRun() {
  return useContext(ActiveRunContext);
}

export function ActiveRunProvider({ children }: { children: React.ReactNode }) {
  const queryClient = useQueryClient();
  const [moduleRun, setModuleRun] = useState<ModuleRunStatus | null>(null);
  const [individualRun, setIndividualRun] = useState<IndividualRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  const initialized = useRef(false);

  // ── On mount, recover any active run after page refresh ──
  const { data: activeRunData } = useQuery({
    queryKey: ['activeRun'],
    queryFn: fetchActiveRun,
    enabled: !initialized.current,
    retry: false,
  });

  useEffect(() => {
    if (activeRunData && !initialized.current) {
      initialized.current = true;
      if (activeRunData.type === 'module' && activeRunData.moduleRun) {
        setModuleRun(activeRunData.moduleRun);
      } else if (activeRunData.type === 'testset' && activeRunData.run) {
        setIndividualRun({
          runId: activeRunData.run.runId,
          testSetId: activeRunData.run.testSetId || '',
          moduleId: undefined,
        });
      }
    }
  }, [activeRunData]);

  // ── Module-level run polling ──
  const { data: polledModuleStatus } = useQuery({
    queryKey: ['moduleRunStatus', moduleRun?.moduleId],
    queryFn: () => fetchModuleRunStatus(moduleRun!.moduleId),
    enabled: !!moduleRun && moduleRun.status === 'Running',
    refetchInterval: 3000,
  });

  useEffect(() => {
    if (polledModuleStatus &&
        (polledModuleStatus.completedCount !== moduleRun?.completedCount ||
         polledModuleStatus.status !== moduleRun?.status ||
         JSON.stringify(polledModuleStatus.currentTestSetIds) !== JSON.stringify(moduleRun?.currentTestSetIds))) {
      setModuleRun(polledModuleStatus);
    }
  }, [polledModuleStatus?.completedCount, polledModuleStatus?.status,
      JSON.stringify(polledModuleStatus?.currentTestSetIds)]);

  useEffect(() => {
    if (moduleRun && moduleRun.status !== 'Running') {
      queryClient.invalidateQueries({ queryKey: ['modules'] });
      queryClient.invalidateQueries({ queryKey: ['moduleTestSets', moduleRun.moduleId] });
      queryClient.invalidateQueries({ queryKey: ['module', moduleRun.moduleId] });
      for (const ts of moduleRun.testSets) {
        queryClient.invalidateQueries({ queryKey: ['runs', moduleRun.moduleId, ts.testSetId] });
        queryClient.invalidateQueries({ queryKey: ['testSet', moduleRun.moduleId, ts.testSetId] });
      }
    }
  }, [moduleRun?.status, moduleRun?.moduleId, queryClient]);

  // ── Individual run polling ──
  const { data: individualRunStatusData } = useQuery({
    queryKey: ['runStatus', individualRun?.runId],
    queryFn: () => fetchRunStatus(individualRun!.runId),
    enabled: !!individualRun,
    refetchInterval: 3000,
  });

  // When individual run reaches a terminal state, clear it and invalidate
  useEffect(() => {
    if (!individualRunStatusData || !individualRun) return;
    const s = individualRunStatusData.status;
    if (s === 'Completed' || s === 'Failed' || s === 'Cancelled') {
      const { moduleId, testSetId } = individualRun;
      setIndividualRun(null);
      if (testSetId) {
        queryClient.invalidateQueries({ queryKey: ['testSet', moduleId, testSetId] });
        queryClient.invalidateQueries({ queryKey: ['runs', moduleId, testSetId] });
      }
      queryClient.invalidateQueries({ queryKey: ['queue'] });
    }
  }, [individualRunStatusData?.status]);

  // ── Helpers ──
  const isModuleRunning = useCallback(
    (moduleId: string) => moduleRun?.moduleId === moduleId && moduleRun?.status === 'Running',
    [moduleRun?.moduleId, moduleRun?.status]
  );

  const isTestSetRunning = useCallback(
    (testSetId: string) => individualRun?.testSetId === testSetId,
    [individualRun?.testSetId]
  );

  const startModuleRunFn = useCallback(async (moduleId: string) => {
    setError(null);
    try {
      const res = await triggerModuleRun(moduleId);
      setModuleRun({
        moduleRunId: res.moduleRunId,
        moduleId: res.moduleId,
        moduleName: '',
        status: 'Running',
        startedAt: res.startedAt,
        completedAt: null,
        error: null,
        completedCount: 0,
        totalCount: res.totalTestSets,
        currentTestSetIds: [],
        currentTestSetId: null,
        testSets: [],
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start module run');
    }
  }, []);

  const value = useMemo<ActiveRunContextValue>(() => ({
    moduleRun,
    isModuleRunning,
    startModuleRun: startModuleRunFn,
    individualRun,
    individualRunStatus: individualRunStatusData ?? null,
    setIndividualRun,
    isTestSetRunning,
    error,
  }), [moduleRun, isModuleRunning, startModuleRunFn, individualRun, individualRunStatusData, isTestSetRunning, error]);

  return (
    <ActiveRunContext.Provider value={value}>
      {children}
    </ActiveRunContext.Provider>
  );
}
