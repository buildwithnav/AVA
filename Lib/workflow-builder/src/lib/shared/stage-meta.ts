import { StageType } from '../models';

export interface StageMeta {
  icon: string;
  color: string;
  label: string;
}

/**
 * Single source of truth for stage icon/color/label styling.
 * Used by the Stages tab (stage-list) and the Test tab (live-preview)
 * so the two views never drift apart.
 */
export const STAGE_META: Record<StageType, StageMeta> = {
  'ai-extract':   { icon: 'psychology',   color: '#1565c0', label: 'AI Extract' },
  'tool':         { icon: 'build',        color: '#2e7d32', label: 'Tool Call' },
  'ai-generate':  { icon: 'auto_awesome', color: '#6a1b9a', label: 'AI Generate' },
  'ai-analyze':   { icon: 'analytics',    color: '#e65100', label: 'AI Analyze' },
  'user-select':  { icon: 'touch_app',    color: '#00838f', label: 'User Select' },
  'confirmation': { icon: 'help_outline', color: '#ad1457', label: 'Confirmation' },
  'foreach':      { icon: 'loop',         color: '#4e342e', label: 'For Each' },
  'agentic':      { icon: 'smart_toy',    color: '#00695c', label: 'Agentic' },
};

/**
 * Stage types hidden from "add stage" menus. Existing workflows using these
 * types still render and edit normally — only authoring new ones is blocked.
 */
export const HIDDEN_STAGE_TYPES: readonly StageType[] = ['agentic'];

export function isStageTypeHidden(type: StageType): boolean {
  return HIDDEN_STAGE_TYPES.includes(type);
}

export const DEFAULT_STAGE_META: StageMeta = {
  icon: 'help_outline',
  color: '#757575',
  label: 'Unknown',
};

/** Resolve stage styling by type, falling back to a neutral default. */
export function getStageMeta(type: string): StageMeta {
  return (STAGE_META as Record<string, StageMeta>)[type] ?? DEFAULT_STAGE_META;
}
