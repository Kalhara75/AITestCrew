import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { AgentSummary } from '../types';

// ── Mocks ────────────────────────────────────────────────────────────────────

// Mock useQuery so we can control what fetchAgents returns without a network.
vi.mock('@tanstack/react-query', () => ({
  useQuery: vi.fn(),
}));

// Mock useAuth so the component can resolve the current user without AuthProvider.
vi.mock('../contexts/AuthContext', () => ({
  useAuth: vi.fn(),
}));

// fetchAgents is only used as a queryFn reference; its actual call is handled
// by the mocked useQuery, so we just need it to exist.
vi.mock('../api/agents', () => ({
  fetchAgents: vi.fn(),
}));

import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../contexts/AuthContext';
import { AgentPicker } from './AgentPicker';

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeAgent(overrides: Partial<AgentSummary>): AgentSummary {
  return {
    id: 'agent-id',
    name: 'Test Agent',
    userId: null,
    ownerName: null,
    capabilities: [],
    version: null,
    status: 'Online',
    role: 'Execution',
    tags: [],
    isShared: false,
    lastSeenAt: '2026-05-16T10:00:00Z',
    registeredAt: '2026-05-01T00:00:00Z',
    currentJob: null,
    ...overrides,
  };
}

function setupMocks(agents: AgentSummary[], userId: string | null = 'alice') {
  (useQuery as ReturnType<typeof vi.fn>).mockReturnValue({ data: agents });
  (useAuth as ReturnType<typeof vi.fn>).mockReturnValue({
    user: userId ? { id: userId, name: 'Alice', role: 'User' } : null,
    apiKey: 'test-key',
    authRequired: false,
    isAdmin: false,
    isAuthSteward: false,
    login: vi.fn(),
    logout: vi.fn(),
    isLoading: false,
  });
}

// ── Tests ─────────────────────────────────────────────────────────────────────

describe('AgentPicker', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('auto-selects the current user\'s own agent when value is null', () => {
    const agents = [
      makeAgent({ id: 'alice-pc', name: 'Alice PC', userId: 'alice', lastSeenAt: '2026-05-16T10:00:00Z' }),
      makeAgent({ id: 'bob-pc',   name: 'Bob PC',   userId: 'bob',   lastSeenAt: '2026-05-16T09:00:00Z' }),
    ];
    setupMocks(agents);

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    expect(onChange).toHaveBeenCalledWith('alice-pc');
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('stays on null when user has no matching agent online', () => {
    const agents = [
      makeAgent({ id: 'bob-pc', name: 'Bob PC', userId: 'bob' }),
    ];
    setupMocks(agents);

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    expect(onChange).not.toHaveBeenCalled();
  });

  it('does not override a pinned (non-null) value', () => {
    const agents = [
      makeAgent({ id: 'alice-pc', name: 'Alice PC', userId: 'alice' }),
    ];
    setupMocks(agents);

    const onChange = vi.fn();
    render(<AgentPicker value="bob-pc" onChange={onChange} />);

    expect(onChange).not.toHaveBeenCalled();
  });

  it('option labels do not contain [Both] or [Execution]', () => {
    const agents = [
      makeAgent({ id: 'agent-a', name: 'Agent A', role: 'Both',      userId: 'other1' }),
      makeAgent({ id: 'agent-b', name: 'Agent B', role: 'Execution', userId: 'other2' }),
    ];
    setupMocks(agents, 'alice');

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    const select = screen.getByRole('combobox');
    const options = Array.from(select.querySelectorAll('option'));
    const labelTexts = options.map(o => o.textContent ?? '');

    expect(labelTexts.some(t => t.includes('['))).toBe(false);
  });

  it('shows (you) on the current user\'s own agent option', () => {
    const agents = [
      makeAgent({ id: 'alice-pc', name: 'Alice PC', userId: 'alice' }),
      makeAgent({ id: 'bob-pc',   name: 'Bob PC',   userId: 'bob'   }),
    ];
    setupMocks(agents);

    const onChange = vi.fn();
    render(<AgentPicker value="alice-pc" onChange={onChange} />);

    const select = screen.getByRole('combobox');
    const options = Array.from(select.querySelectorAll('option'));
    const aliceOption = options.find(o => o.value === 'alice-pc');
    const bobOption   = options.find(o => o.value === 'bob-pc');

    expect(aliceOption?.textContent).toBe('Alice PC (you)');
    expect(bobOption?.textContent).toBe('Bob PC');
  });

  it('picks the most-recently-seen agent when user has multiple', () => {
    const agents = [
      makeAgent({ id: 'alice-old', name: 'Alice Old', userId: 'alice', lastSeenAt: '2026-05-15T08:00:00Z' }),
      makeAgent({ id: 'alice-new', name: 'Alice New', userId: 'alice', lastSeenAt: '2026-05-16T10:00:00Z' }),
    ];
    setupMocks(agents);

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    expect(onChange).toHaveBeenCalledWith('alice-new');
  });

  it('does not auto-select when user is null (unauthenticated)', () => {
    const agents = [
      makeAgent({ id: 'alice-pc', name: 'Alice PC', userId: 'alice' }),
    ];
    setupMocks(agents, null);   // no authenticated user

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    expect(onChange).not.toHaveBeenCalled();
  });

  it('renders the tooltip title on the select element', () => {
    setupMocks([]);

    const onChange = vi.fn();
    render(<AgentPicker value={null} onChange={onChange} />);

    const select = screen.getByRole('combobox');
    expect(select).toHaveAttribute('title');
    expect(select.getAttribute('title')).toContain('Defaults to your machine');
  });
});
