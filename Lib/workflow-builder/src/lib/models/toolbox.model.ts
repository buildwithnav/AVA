// ─── Toolbox Bundle ─────────────────────────────────────────────
// Portable export/import format inspired by Microsoft Foundry Toolbox.
// A bundle contains everything needed to reproduce a set of workflows
// in another AVA instance: prompts, referenced tools, and templates.

import { PromptDefinition } from './prompt.model';
import { ToolDefinition } from './tool.model';
import { TemplateDefinition } from './template.model';

export interface ToolboxBundle {
  /** Schema identifier for forward compatibility */
  $schema: 'ava-toolbox/v1';

  /** Domain this bundle belongs to */
  domain: string;

  /** Semantic version of this bundle */
  version: string;

  /** ISO 8601 export timestamp */
  exportedAt: string;

  /** Prompt/workflow definitions included */
  prompts: PromptDefinition[];

  /** Only tools referenced by the included prompts */
  tools: ToolDefinition[];

  /** Only templates referenced by the included prompts */
  templates: TemplateDefinition[];

  /** Bundle metadata */
  metadata: BundleMetadata;
}

export interface BundleMetadata {
  createdBy: string;
  description: string;
  tags?: string[];
}

export interface ImportResult {
  added: { prompts: string[]; tools: string[]; templates: string[] };
  updated: { prompts: string[]; tools: string[]; templates: string[] };
  conflicts: ImportConflict[];
  warnings: string[];
}

export interface ImportConflict {
  type: 'prompt' | 'tool' | 'template';
  id: string;
  reason: string;
}

/** Create an empty bundle for a given domain */
export function createEmptyBundle(domain: string, createdBy: string): ToolboxBundle {
  return {
    $schema: 'ava-toolbox/v1',
    domain,
    version: '1.0.0',
    exportedAt: new Date().toISOString(),
    prompts: [],
    tools: [],
    templates: [],
    metadata: {
      createdBy,
      description: '',
    },
  };
}

/**
 * Collect referenced tool/template IDs from a set of prompts.
 * Walks through all stages to find toolId references and templateRef values.
 */
export function collectReferences(prompts: PromptDefinition[]): {
  toolIds: Set<string>;
  templateIds: Set<string>;
} {
  const toolIds = new Set<string>();
  const templateIds = new Set<string>();

  function walkStages(stages: PromptDefinition['stages']): void {
    if (!stages) return;
    for (const stage of stages) {
      switch (stage.type) {
        case 'tool':
          if (stage.toolId) toolIds.add(stage.toolId);
          if (stage.prePrompt?.allowedToolIds) {
            stage.prePrompt.allowedToolIds.forEach((id) => toolIds.add(id));
          }
          break;
        case 'ai-generate':
          if (stage.templateRef) templateIds.add(stage.templateRef);
          break;
        case 'foreach':
          walkStages(stage.stages);
          break;
      }
    }
  }

  for (const prompt of prompts) {
    walkStages(prompt.stages);
  }

  return { toolIds, templateIds };
}
