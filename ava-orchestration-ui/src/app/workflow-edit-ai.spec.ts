import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  WorkflowCreatorService,
  CreationStageCard,
  cardFromPayload,
  findCardInTree,
  insertCardInTree,
  updateCardInTree,
  removeCardFromTree,
  reorderCardsInScope,
  findDuplicateStageIds,
} from '@ava/workflow-builder/services/workflow-creator.service';
import { nextVersion } from '@ava/workflow-builder/components/creator/workflow-creation-view.component';
import { PromptDefinition } from '@ava/workflow-builder';

/** A realistic workflow containing a foreach with a nested tool stage + token refs. */
function sampleForeachPrompt(): PromptDefinition {
  return {
    id: 'tasks-per-sprint-pbi',
    domain: 'ado',
    domains: ['ado'],
    description: 'Create a child task under each PBI in the current sprint',
    samplePrompt: 'add a task under each PBI',
    contextTags: ['sprint', 'tasks'],
    systemPrompt: 'You create child tasks for sprint PBIs.',
    piiCheck: false,
    version: '1.2',
    stages: [
      { stageId: 'extract', type: 'ai-extract', prompt: 'Extract the sprint name.', required: ['sprint'] },
      { stageId: 'fetchPbis', type: 'tool', toolId: 'ado-query-sprint-pbis', parameters: { sprint: '{{sprint}}' }, outputMap: { pbis: 'value' } },
      { stageId: 'confirm', type: 'confirmation', prompt: 'Create a task under each of {{pbis}}?' },
      {
        stageId: 'perPbi',
        type: 'foreach',
        itemsFrom: 'pbis',
        itemAlias: 'pbi',
        confirmEach: false,
        stages: [
          { stageId: 'createTask', type: 'tool', toolId: 'ado-create-task', parameters: { parentId: '{{pbi.id}}', title: 'Task for {{pbi.title}}' } },
        ],
      },
      { stageId: 'summarize', type: 'ai-analyze', prompt: 'Summarize the created tasks.' },
    ],
  };
}

describe('Edit-with-AI: foreach round-trip', () => {
  let creator: WorkflowCreatorService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient()] });
    creator = TestBed.inject(WorkflowCreatorService);
  });

  it('preserves foreach structure through promptToDraft -> asPromptDefinition', () => {
    const original = sampleForeachPrompt();
    creator.startEditSession(original);
    const result = creator.asPromptDefinition();

    expect(result.id).toBe('tasks-per-sprint-pbi');
    expect(result.stages?.length).toBe(5);

    const foreach: any = result.stages?.find(s => s.stageId === 'perPbi');
    expect(foreach.type).toBe('foreach');
    expect(foreach.itemsFrom).toBe('pbis');
    expect(foreach.itemAlias).toBe('pbi');
    expect(foreach.stages.length).toBe(1);

    const nested = foreach.stages[0];
    expect(nested.stageId).toBe('createTask');
    expect(nested.type).toBe('tool');
    expect(nested.toolId).toBe('ado-create-task');
    expect(nested.parameters.parentId).toBe('{{pbi.id}}');
  });

  it('locks identity and produces no error-level coherence warnings for valid loop refs', () => {
    creator.startEditSession(sampleForeachPrompt());
    // {{pbi.id}} and {{pbi.title}} are valid inside the loop -> no unresolved-token warnings
    const unresolved = creator.coherenceWarnings().filter(w => w.type === 'unresolved_token');
    expect(unresolved.length).toBe(0);
    expect(creator.editingId()).toBe('tasks-per-sprint-pbi');
    expect(creator.originalVersion()).toBe('1.2');
  });

  it('preserves per-type fields that are not part of the common set', () => {
    const original: PromptDefinition = {
      id: 'rich', domain: 'ado', domains: ['ado'], description: 'd', contextTags: ['t'],
      systemPrompt: 's', piiCheck: false,
      stages: [
        { stageId: 'x', type: 'ai-extract', prompt: 'p', required: ['a'], fields: [{ name: 'a', type: 'string' }] },
        { stageId: 'gen', type: 'ai-generate', prompt: 'g', rewindTo: 'x' },
        { stageId: 'an', type: 'ai-analyze', prompt: 'a', dataFrom: ['_result_tool'] },
        { stageId: 'sel', type: 'user-select', optionsFrom: 'opts', labelField: 'name' },
        { stageId: 'conf', type: 'confirmation', prompt: 'ok?', rewindTo: 'gen' },
        { stageId: 'ag', type: 'agentic', prompt: 'explore', toolDomains: ['ado'], maxIterations: 7, outputField: 'summary' },
      ],
    };
    creator.startEditSession(original);
    const out = creator.asPromptDefinition();
    const byId = (id: string) => out.stages!.find(s => s.stageId === id) as any;

    expect(byId('x').fields[0].name).toBe('a');
    expect(byId('gen').rewindTo).toBe('x');
    expect(byId('an').dataFrom).toEqual(['_result_tool']);
    expect(byId('sel').labelField).toBe('name');
    expect(byId('conf').rewindTo).toBe('gen');
    expect(byId('ag').toolDomains).toEqual(['ado']);
    expect(byId('ag').maxIterations).toBe(7);
    expect(byId('ag').outputField).toBe('summary');
  });
});

describe('Edit-with-AI: stage-tree helpers', () => {
  function tree(): CreationStageCard[] {
    return [
      { stageId: 'a', type: 'tool', toolId: 't1' },
      { stageId: 'loop', type: 'foreach', itemsFrom: 'x', itemAlias: 'i', subStages: [
        { stageId: 'inner', type: 'tool', toolId: 't2' },
      ] },
    ];
  }

  it('cardFromPayload maps nested foreach stages array into subStages', () => {
    const card = cardFromPayload({ stageId: 'loop', type: 'foreach', itemsFrom: 'x', itemAlias: 'i', stages: [
      { stageId: 'child', type: 'tool', toolId: 't' },
    ] });
    expect(card.subStages?.length).toBe(1);
    expect(card.subStages?.[0].stageId).toBe('child');
  });

  it('findCardInTree finds nested sub-stages', () => {
    expect(findCardInTree(tree(), 'inner')?.toolId).toBe('t2');
    expect(findCardInTree(tree(), 'missing')).toBeNull();
  });

  it('insertCardInTree adds a stage inside a foreach via parentStageId', () => {
    const next = insertCardInTree(tree(), { stageId: 'inner2', type: 'ai-analyze' }, 'loop', 'inner');
    const loop = next.find(s => s.stageId === 'loop')!;
    expect(loop.subStages?.map(s => s.stageId)).toEqual(['inner', 'inner2']);
  });

  it('updateCardInTree updates a nested sub-stage by id', () => {
    const next = updateCardInTree(tree(), 'inner', { toolId: 'changed' });
    expect(findCardInTree(next, 'inner')?.toolId).toBe('changed');
  });

  it('removeCardFromTree removes a nested sub-stage by id', () => {
    const next = removeCardFromTree(tree(), 'inner');
    expect(findCardInTree(next, 'inner')).toBeNull();
    expect(findCardInTree(next, 'loop')).not.toBeNull();
  });

  it('reorderCardsInScope reorders sub-stages inside a foreach', () => {
    const base = insertCardInTree(tree(), { stageId: 'inner2', type: 'ai-analyze' }, 'loop');
    const next = reorderCardsInScope(base, ['inner2', 'inner'], 'loop');
    const loop = next.find(s => s.stageId === 'loop')!;
    expect(loop.subStages?.map(s => s.stageId)).toEqual(['inner2', 'inner']);
  });

  it('findDuplicateStageIds detects ids reused across loop scope', () => {
    const dup: CreationStageCard[] = [
      { stageId: 'x', type: 'tool' },
      { stageId: 'loop', type: 'foreach', subStages: [{ stageId: 'x', type: 'tool' }] },
    ];
    expect(findDuplicateStageIds(dup)).toContain('x');
  });
});

describe('Edit-with-AI: version bump', () => {
  it('bumps the patch segment', () => {
    expect(nextVersion('1.2')).toBe('1.3');
    expect(nextVersion('2.0.4')).toBe('2.0.5');
  });
  it('defaults when no version exists', () => {
    expect(nextVersion(undefined)).toBe('1.1');
  });
});
