import { useEffect } from 'react';

type Combo = string;
type Handler = (e: KeyboardEvent) => void;

// Combo format: "Escape" | "Mod+/" | "Mod+Shift+K" — "Mod" matches Cmd on mac
// and Ctrl elsewhere. Plain non-modifier combos are skipped while typing in
// inputs/textareas/contenteditable; Escape is always delivered.

function matches(combo: Combo, e: KeyboardEvent): boolean {
  const parts = combo.split('+').map(p => p.trim());
  const key = parts[parts.length - 1];
  const mods = new Set(parts.slice(0, -1).map(p => p.toLowerCase()));
  if (mods.has('mod')   !== (e.ctrlKey || e.metaKey)) return false;
  if (mods.has('shift') !== e.shiftKey) return false;
  if (mods.has('alt')   !== e.altKey) return false;
  if (key.length === 1) return e.key.toLowerCase() === key.toLowerCase();
  return e.key === key;
}

function isEditable(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false;
  if (el.isContentEditable) return true;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
}

export function useKeyboardShortcuts(map: Record<Combo, Handler>, enabled = true): void {
  useEffect(() => {
    if (!enabled) return;
    const onKeyDown = (e: KeyboardEvent) => {
      const editable = isEditable(e.target);
      for (const combo of Object.keys(map)) {
        if (!matches(combo, e)) continue;
        // Allow Escape and any combo with a modifier to fire even from inputs.
        const hasModifier = /\bMod\b|\bShift\b|\bAlt\b/i.test(combo);
        if (editable && combo !== 'Escape' && !hasModifier) continue;
        map[combo](e);
        return;
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [map, enabled]);
}
