import type { CSSProperties, ReactNode } from 'react';
import {
  ArrowUpRightIcon, PlayIcon, PlusIcon, RecordIcon, ClipboardListIcon, TableIcon,
} from '../icons';
import { tokens } from '../tokens';
import { confirmHeaderStyle } from '../styles';

export interface ActionMeta {
  icon: ReactNode;
  tint: string;
  label: string;
}

export const ACTION_META: Record<string, ActionMeta> = {
  navigate: {
    icon: <ArrowUpRightIcon />,
    tint: tokens.color.navTint,
    label: 'Navigate',
  },
  showData: {
    icon: <TableIcon />,
    tint: tokens.color.dataTint,
    label: 'Data',
  },
  confirmRun: {
    icon: <PlayIcon />,
    tint: tokens.color.runTint,
    label: 'Confirm run',
  },
  confirmCreate: {
    icon: <PlusIcon />,
    tint: tokens.color.createTint,
    label: 'Confirm create',
  },
  confirmRecord: {
    icon: <RecordIcon />,
    tint: tokens.color.recordTint,
    label: 'Confirm recording',
  },
  confirmCreatePostStep: {
    icon: <ClipboardListIcon />,
    tint: tokens.color.postTint,
    label: 'Add post-step',
  },
};

export function actionCardStyle(tint: string): CSSProperties {
  return {
    background: tokens.color.bg,
    border: `1px solid ${tokens.color.border}`,
    borderLeft: `4px solid ${tint}`,
    borderRadius: tokens.radius.lg,
    padding: 10,
    minWidth: 0,
  };
}

export function ActionCardHeader({ kind, summary }: { kind: string; summary?: string }) {
  const meta = ACTION_META[kind];
  if (!meta) return null;
  return (
    <>
      <div style={{ ...confirmHeaderStyle, color: meta.tint }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', color: meta.tint }}>
          {meta.icon}
        </span>
        <span>{meta.label}</span>
      </div>
      {summary && (
        <div style={{
          fontSize: tokens.font.size.md,
          color: tokens.color.text,
          marginBottom: 6,
        }}>
          {summary}
        </div>
      )}
    </>
  );
}
