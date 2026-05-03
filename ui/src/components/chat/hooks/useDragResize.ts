import { useCallback, useRef, useState } from 'react';
import type { CSSProperties, KeyboardEvent, PointerEvent } from 'react';
import { usePersistedNumber } from './usePersistedNumber';

interface DragResizeOptions {
  axis: 'x' | 'y';
  // When true, dragging in the negative axis direction increases the value
  // (e.g. drawer left edge dragged left → wider drawer).
  invert?: boolean;
  min: number;
  max: number;
  defaultValue: number;
  storageKey: string;
  // Pixels nudged per arrow keypress for keyboard a11y.
  step?: number;
}

interface DragResizeHandleProps {
  role: 'separator';
  'aria-orientation': 'vertical' | 'horizontal';
  'aria-valuemin': number;
  'aria-valuemax': number;
  'aria-valuenow': number;
  tabIndex: 0;
  onPointerDown: (e: PointerEvent<HTMLElement>) => void;
  onKeyDown: (e: KeyboardEvent<HTMLElement>) => void;
  style: CSSProperties;
}

interface DragResizeReturn {
  value: number;
  setValue: (next: number) => void;
  isDragging: boolean;
  handleProps: DragResizeHandleProps;
}

function clamp(n: number, min: number, max: number) {
  return Math.max(min, Math.min(max, n));
}

export function useDragResize(opts: DragResizeOptions): DragResizeReturn {
  const { axis, invert = false, min, max, defaultValue, storageKey, step = 16 } = opts;
  const [persisted, setPersisted] = usePersistedNumber(storageKey, defaultValue);
  const [live, setLive] = useState<number>(persisted);
  const [isDragging, setIsDragging] = useState(false);

  // Drag state
  const startPos = useRef(0);
  const startValue = useRef(0);

  const setValue = useCallback((next: number) => {
    const clamped = clamp(next, min, max);
    setLive(clamped);
    setPersisted(clamped);
  }, [min, max, setPersisted]);

  const onPointerDown = useCallback((e: PointerEvent<HTMLElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    startPos.current = axis === 'x' ? e.clientX : e.clientY;
    startValue.current = live;
    setIsDragging(true);

    const handleMove = (ev: globalThis.PointerEvent) => {
      const current = axis === 'x' ? ev.clientX : ev.clientY;
      const delta = current - startPos.current;
      const next = invert ? startValue.current - delta : startValue.current + delta;
      setLive(clamp(next, min, max));
    };

    const handleUp = (ev: globalThis.PointerEvent) => {
      window.removeEventListener('pointermove', handleMove);
      window.removeEventListener('pointerup', handleUp);
      window.removeEventListener('pointercancel', handleUp);
      setIsDragging(false);
      const current = axis === 'x' ? ev.clientX : ev.clientY;
      const delta = current - startPos.current;
      const next = invert ? startValue.current - delta : startValue.current + delta;
      setPersisted(clamp(next, min, max));
    };

    window.addEventListener('pointermove', handleMove);
    window.addEventListener('pointerup', handleUp);
    window.addEventListener('pointercancel', handleUp);
  }, [axis, invert, live, min, max, setPersisted]);

  const onKeyDown = useCallback((e: KeyboardEvent<HTMLElement>) => {
    let next: number | null = null;
    if (axis === 'x') {
      if (e.key === 'ArrowLeft')  next = invert ? live + step : live - step;
      if (e.key === 'ArrowRight') next = invert ? live - step : live + step;
    } else {
      if (e.key === 'ArrowUp')   next = invert ? live + step : live - step;
      if (e.key === 'ArrowDown') next = invert ? live - step : live + step;
    }
    if (e.key === 'Home') next = min;
    if (e.key === 'End')  next = max;
    if (next != null) {
      e.preventDefault();
      setValue(next);
    }
  }, [axis, invert, live, min, max, step, setValue]);

  const handleProps: DragResizeHandleProps = {
    role: 'separator',
    'aria-orientation': axis === 'x' ? 'vertical' : 'horizontal',
    'aria-valuemin': min,
    'aria-valuemax': max,
    'aria-valuenow': Math.round(live),
    tabIndex: 0,
    onPointerDown,
    onKeyDown,
    style: {
      cursor: axis === 'x' ? 'ew-resize' : 'ns-resize',
      touchAction: 'none',
      userSelect: 'none',
    },
  };

  return { value: live, setValue, isDragging, handleProps };
}
