import { useEffect, useState } from 'react';
import { tokens } from './tokens';

const QUERY = '(prefers-reduced-motion: reduce)';

export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() =>
    typeof window !== 'undefined' && window.matchMedia
      ? window.matchMedia(QUERY).matches
      : false);

  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const mql = window.matchMedia(QUERY);
    const handler = (e: MediaQueryListEvent) => setReduced(e.matches);
    mql.addEventListener('change', handler);
    return () => mql.removeEventListener('change', handler);
  }, []);

  return reduced;
}

export const transitions = {
  base: `${tokens.motion.base}ms ${tokens.motion.ease}`,
  fast: `${tokens.motion.fast}ms ${tokens.motion.ease}`,
  slow: `${tokens.motion.slow}ms ${tokens.motion.ease}`,
};
