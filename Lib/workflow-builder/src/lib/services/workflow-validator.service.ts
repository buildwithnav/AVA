import { Injectable } from '@angular/core';
import {
  PromptDefinition,
  WorkflowStage,
  ToolDefinition,
  TemplateDefinition,
  StageType,
} from '../models';

export interface ValidationError {
  path: string;
  message: string;
  severity: 'error' | 'warning';
}

/**
 * Validates workflow definitions for correctness and completeness.
 * Checks for missing required fields, dangling references, and structural issues.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowValidatorService {

  /** Validate a complete prompt definition */
  validatePrompt(
    prompt: PromptDefinition,
    tools: ToolDefinition[],
    templates: TemplateDefinition[]
  ): ValidationError[] {
    const errors: ValidationError[] = [];
    const toolIds = new Set(tools.map((t) => t.id));
    const templateIds = new Set(templates.map((t) => t.id));

    if (!prompt.id.trim()) {
      errors.push({ path: 'id', message: 'Prompt ID is required', severity: 'error' });
    }
    if (!prompt.domain.trim()) {
      errors.push({ path: 'domain', message: 'Domain is required', severity: 'error' });
    }
    if (!prompt.description.trim()) {
      errors.push({ path: 'description', message: 'Description is required', severity: 'warning' });
    }
    if (!prompt.systemPrompt.trim()) {
      errors.push({ path: 'systemPrompt', message: 'System prompt is required', severity: 'error' });
    }
    if (prompt.contextTags.length === 0) {
      errors.push({ path: 'contextTags', message: 'At least one context tag is recommended', severity: 'warning' });
    }

    if (prompt.stages) {
      this.validateStages(prompt.stages, toolIds, templateIds, 'stages', errors);
    }

    return errors;
  }

  /** Validate a list of stages (recursive for foreach) */
  private validateStages(
    stages: WorkflowStage[],
    toolIds: Set<string>,
    templateIds: Set<string>,
    basePath: string,
    errors: ValidationError[]
  ): void {
    const stageIds = new Set<string>();

    for (let i = 0; i < stages.length; i++) {
      const stage = stages[i];
      const path = `${basePath}[${i}]`;

      // Duplicate stageId check
      if (!stage.stageId.trim()) {
        errors.push({ path: `${path}.stageId`, message: 'Stage ID is required', severity: 'error' });
      } else if (stageIds.has(stage.stageId)) {
        errors.push({ path: `${path}.stageId`, message: `Duplicate stage ID "${stage.stageId}"`, severity: 'error' });
      }
      stageIds.add(stage.stageId);

      this.validateStage(stage, toolIds, templateIds, path, errors);
    }
  }

  /** Validate a single stage by type */
  private validateStage(
    stage: WorkflowStage,
    toolIds: Set<string>,
    templateIds: Set<string>,
    path: string,
    errors: ValidationError[]
  ): void {
    switch (stage.type) {
      case 'ai-extract':
        if (!stage.prompt.trim()) {
          errors.push({ path: `${path}.prompt`, message: 'Extract prompt is required', severity: 'error' });
        }
        break;

      case 'tool':
        if (!stage.toolId && !stage.prePrompt) {
          errors.push({ path: `${path}`, message: 'Tool stage needs either toolId or prePrompt', severity: 'error' });
        }
        if (stage.toolId && !toolIds.has(stage.toolId)) {
          errors.push({ path: `${path}.toolId`, message: `Tool "${stage.toolId}" not found in registry`, severity: 'error' });
        }
        if (stage.prePrompt) {
          if (!stage.prePrompt.prompt.trim()) {
            errors.push({ path: `${path}.prePrompt.prompt`, message: 'PrePrompt prompt text is required', severity: 'error' });
          }
          for (const tid of stage.prePrompt.allowedToolIds ?? []) {
            if (!toolIds.has(tid)) {
              errors.push({ path: `${path}.prePrompt.allowedToolIds`, message: `Allowed tool "${tid}" not found`, severity: 'error' });
            }
          }
        }
        break;

      case 'ai-generate':
        if (!stage.prompt.trim()) {
          errors.push({ path: `${path}.prompt`, message: 'Generate prompt is required', severity: 'error' });
        }
        if (stage.templateRef && !templateIds.has(stage.templateRef)) {
          errors.push({ path: `${path}.templateRef`, message: `Template "${stage.templateRef}" not found`, severity: 'error' });
        }
        break;

      case 'ai-analyze':
        if (!stage.prompt.trim()) {
          errors.push({ path: `${path}.prompt`, message: 'Analyze prompt is required', severity: 'error' });
        }
        break;

      case 'user-select':
        if (!stage.optionsFrom.trim()) {
          errors.push({ path: `${path}.optionsFrom`, message: 'optionsFrom path is required', severity: 'error' });
        }
        break;

      case 'confirmation':
        if (!stage.prompt.trim()) {
          errors.push({ path: `${path}.prompt`, message: 'Confirmation prompt is required', severity: 'error' });
        }
        break;

      case 'foreach':
        if (!stage.itemsFrom.trim()) {
          errors.push({ path: `${path}.itemsFrom`, message: 'itemsFrom path is required', severity: 'error' });
        }
        if (!stage.itemAlias.trim()) {
          errors.push({ path: `${path}.itemAlias`, message: 'itemAlias is required', severity: 'error' });
        }
        if (stage.stages.length === 0) {
          errors.push({ path: `${path}.stages`, message: 'ForEach needs at least one sub-stage', severity: 'warning' });
        } else {
          this.validateStages(stage.stages, toolIds, templateIds, `${path}.stages`, errors);
        }
        break;
    }
  }

  /** Quick check if a prompt has any validation errors (not warnings) */
  hasErrors(
    prompt: PromptDefinition,
    tools: ToolDefinition[],
    templates: TemplateDefinition[]
  ): boolean {
    return this.validatePrompt(prompt, tools, templates)
      .some((e) => e.severity === 'error');
  }

  /**
   * Checks context tag uniqueness across all prompts that share at least one domain
   * with the prompt being edited. Returns errors for any tag that is already used by
   * another workflow in the same domain.
   *
   * @param originalId The ID the prompt was originally loaded under. Pass this when
   *   the user has renamed the prompt so the old entry in allPrompts is still excluded
   *   from the conflict check (avoids false positives during a rename).
   */
  validateContextTagUniqueness(
    prompt: PromptDefinition,
    allPrompts: PromptDefinition[],
    originalId?: string
  ): ValidationError[] {
    const errors: ValidationError[] = [];
    if (!prompt.contextTags.length || !allPrompts.length) return errors;

    const currentDomains = new Set<string>([
      ...(prompt.domains ?? []),
      ...(prompt.domain ? [prompt.domain] : []),
    ].map(d => d.toLowerCase()));

    // IDs to treat as "self" — excludes both the current id and the pre-rename original id.
    const selfIds = new Set<string>(
      [prompt.id, originalId].filter(Boolean).map(id => id!.toLowerCase())
    );

    // Build map: tag (lowercase) → first conflicting prompt id
    const tagOwners = new Map<string, string>();
    for (const other of allPrompts) {
      if (selfIds.has(other.id.toLowerCase())) continue;

      const otherDomains = new Set<string>([
        ...(other.domains ?? []),
        ...(other.domain ? [other.domain] : []),
      ].map(d => d.toLowerCase()));

      const sharesADomain = [...currentDomains].some(d => otherDomains.has(d));
      if (!sharesADomain) continue;

      for (const tag of other.contextTags) {
        const key = tag.toLowerCase();
        if (!tagOwners.has(key)) {
          tagOwners.set(key, other.id);
        }
      }
    }

    for (const tag of prompt.contextTags) {
      const owner = tagOwners.get(tag.toLowerCase());
      if (owner) {
        errors.push({
          path: 'contextTags',
          message: `Tag "${tag}" is already used by workflow "${owner}"`,
          severity: 'error',
        });
      }
    }

    return errors;
  }
}
