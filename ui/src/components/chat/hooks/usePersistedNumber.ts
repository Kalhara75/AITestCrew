import { useCallback, useState } from 'react';

export function usePersistedNumber(
  storageKey: string,
  defaultValue: number,
): [number, (next: number) => void] {
  const [value, setValue] = useState<number>(() => {
    if (typeof window === 'undefined') return defaultValue;
    try {
      const raw = window.localStorage.getItem(storageKey);
      if (raw == null) return defaultValue;
      const parsed = Number(raw);
      return Number.isFinite(parsed) ? parsed : defaultValue;
    } catch {
      return defaultValue;
    }
  });

  const set = useCallback((next: number) => {
    setValue(next);
    try {
      window.localStorage.setItem(storageKey, String(next));
    } catch {
      // Ignore — storage may be unavailable (private mode, quota exceeded).
    }
  }, [storageKey]);

  return [value, set];
}
