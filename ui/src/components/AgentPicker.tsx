import { useEffect, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchAgents } from '../api/agents';
import { useAuth } from '../contexts/AuthContext';
import type { AgentSummary } from '../types';

interface Props {
  /** The target type the run will use (e.g. "UI_Web_Blazor") — used to filter capable agents. */
  targetCapability?: string;
  value: string | null;
  onChange: (agentId: string | null) => void;
  disabled?: boolean;
}

const ROLE_COLORS: Record<string, { bg: string; fg: string }> = {
  Recording:  { bg: '#f3e8ff', fg: '#7c3aed' },
  Execution:  { bg: '#eff6ff', fg: '#1d4ed8' },
  Both:       { bg: '#f1f5f9', fg: '#475569' },
};

/** Small role chip rendered inside AgentsPanel (NOT in picker options). */
export function RoleChip({ role }: { role: string }) {
  const colours = ROLE_COLORS[role] ?? ROLE_COLORS.Both;
  return (
    <span style={{
      fontSize: 10, fontWeight: 700,
      background: colours.bg, color: colours.fg,
      padding: '1px 7px', borderRadius: 8, whiteSpace: 'nowrap',
    }}>
      {role}
    </span>
  );
}

/** Tag chip — plain grey pill used in AgentsPanel and AgentPicker options. */
export function TagChip({ tag }: { tag: string }) {
  return (
    <span style={{
      fontSize: 10, fontWeight: 500,
      background: '#f1f5f9', color: '#334155',
      padding: '1px 6px', borderRadius: 6, whiteSpace: 'nowrap',
    }}>
      {tag}
    </span>
  );
}

/**
 * Dropdown that lets the user pick a specific execution agent (or "Any execution agent").
 * Filters to agents that are Online/Busy with role Execution|Both and, when
 * `targetCapability` is set, must advertise that capability.
 *
 * Auto-selects the current user's own agent on mount if no value/pin is already set.
 */
export function AgentPicker({ targetCapability, value, onChange, disabled }: Props) {
  const { user } = useAuth();
  const hasAutoSelected = useRef(false);

  const { data: agents } = useQuery({
    queryKey: ['agents'],
    queryFn: fetchAgents,
    refetchInterval: 10000,
    retry: false,
  });

  const candidates = (agents ?? []).filter((a: AgentSummary) => {
    const isOnline = a.status === 'Online' || a.status === 'Busy';
    const isExecution = a.role === 'Execution' || a.role === 'Both';
    const hasCap = !targetCapability || a.capabilities.includes(targetCapability);
    return isOnline && isExecution && hasCap;
  });

  // Auto-select the current user's own online agent on first render.
  // Fires only once per mount (hasAutoSelected ref) so a manual "Any execution agent"
  // selection is not immediately overridden (AC-5).
  useEffect(() => {
    if (hasAutoSelected.current) return;
    if (value !== null) return;           // pinned or user-set — leave alone (AC-4)
    if (!candidates.length) return;
    const mine = candidates
      .filter((a: AgentSummary) => a.userId === user?.id)
      .sort((a: AgentSummary, b: AgentSummary) =>
        (b.lastSeenAt ?? '').localeCompare(a.lastSeenAt ?? ''),
      );
    if (mine.length > 0) {
      hasAutoSelected.current = true;
      onChange(mine[0].id);
    }
  }, [candidates, user?.id]); // intentionally omits value + onChange to avoid re-trigger loop

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <label style={{ fontSize: 12, color: '#475569', fontWeight: 500, whiteSpace: 'nowrap' }}>
        Run on:
      </label>
      <select
        value={value ?? ''}
        onChange={e => onChange(e.target.value || null)}
        disabled={disabled}
        title="Run claims on this agent. Defaults to your machine when online; falls back to any available execution agent."
        style={{
          fontSize: 12, padding: '3px 8px', borderRadius: 6,
          border: '1px solid #cbd5e1', background: '#fff', color: '#0f172a',
          cursor: disabled ? 'not-allowed' : 'pointer',
          opacity: disabled ? 0.6 : 1,
        }}
      >
        <option value="">Any execution agent</option>
        {candidates.map((a: AgentSummary) => (
          <option key={a.id} value={a.id}>
            {a.name}{a.userId === user?.id ? ' (you)' : ''}{a.tags.length > 0 ? ` (${a.tags.join(', ')})` : ''}
          </option>
        ))}
      </select>
    </div>
  );
}
