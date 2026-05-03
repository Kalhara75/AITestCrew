import { useEffect } from 'react';
import type { RefObject } from 'react';

interface Options {
  value: string;
  // Bounds expressed in pixels. The composer container itself is resized
  // separately via useDragResize; this hook only sizes the textarea up to
  // the container's height.
  minHeight: number;
  maxHeight: number;
}

export function useAutoGrowTextarea(
  ref: RefObject<HTMLTextAreaElement | null>,
  { value, minHeight, maxHeight }: Options,
) {
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    // Reset to 0 to get a fresh scrollHeight measurement, then clamp.
    el.style.height = '0px';
    const next = Math.min(Math.max(el.scrollHeight, minHeight), maxHeight);
    el.style.height = `${next}px`;
    el.style.overflowY = el.scrollHeight > maxHeight ? 'auto' : 'hidden';
  }, [ref, value, minHeight, maxHeight]);
}
