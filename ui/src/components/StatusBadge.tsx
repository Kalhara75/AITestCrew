/**
 * Backward-compatibility shim — re-exports StatusBadge from the canonical
 * execution design system. All new code should import from
 * '../components/execution' or '../components/execution/StatusBadge'.
 */
export { StatusBadge, STATUS_COLORS } from './execution/StatusBadge';
