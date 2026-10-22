/**
 * Integration test: Complaints Review Status → KB Queries → Domain Switch
 *
 * Exercises the full flow in a single conversation:
 *  1. Trigger complaints review status report via chat (study domain — the
 *     complaints-review-status prompt belongs to the "study" domain)
 *     - 3-stage workflow: ai-extract → tool (cds-complaints-review-status) → ai-analyze
 *     - The xlsx is auto-stored as a session-scoped KB
 *  2. Query KB for max complaints (same conversationId)
 *  3. Query KB for min complaints (same conversationId)
 *  4. Switch domain to "study" with a study-analysis prompt, handle the
 *     user-select interaction, and get Veritas detailed analysis
 */

import { test, expect } from '@playwright/test';
import {
  streamChat,
  findEvent,
  findEvents,
  getAssistantResponse,
  newConversationId,
  selectAndResume,
  type ChatRequest,
} from './helpers';

// Complaints flow is heavy (xlsx fetch + KB upload + ingestion) — generous timeouts
test.describe('Complaints Flow → KB Queries → Domain Switch', () => {
  const conversationId = newConversationId();

  test.describe.configure({ mode: 'serial' });

  // ── Step 1: Trigger complaints report ────────────────────────────────────

  test('Step 1: fetch complaints review status report via chat', async ({ request }) => {
    test.setTimeout(120_000); // xlsx fetch + KB ingestion can be slow

    const events = await streamChat(request, {
      message: 'Get the complaints review status report for all studies with default parameters. No filters needed, pull everything.',
      conversationId,
      context: {
        domain: 'study', // complaints-review-status prompt lives in "study" domain
        roles: [],
        userId: 'test-user',
      },
    });

    // Should have started an agent run
    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();

    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);
    console.log(`  Tools called: ${toolNames.join(', ')}`);

    // The orchestrator should invoke start_workflow for complaints-review-status
    const usedWorkflow = toolNames.includes('start_workflow');
    expect(usedWorkflow).toBeTruthy();

    // Workflow stages should fire
    const stageStarts = findEvents(events, 'stage_start');
    const stageIds = stageStarts.map((e) => (e.data as { stageId: string }).stageId);
    console.log(`  Workflow stages: ${stageIds.join(' → ')}`);

    // Should complete without error
    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    // Should have a meaningful response (the ai-analyze summary)
    const response = getAssistantResponse(events);
    expect(response.length).toBeGreaterThan(10);
    console.log(`  Response (${response.length} chars): ${response.substring(0, 400)}`);
  });

  // ── Step 2: KB query — max complaints ──────────────────────────────────

  test('Step 2: query KB for max complaints', async ({ request }) => {
    const events = await streamChat(request, {
      message: 'Which site or category has the maximum number of complaints?',
      conversationId,
      context: {
        domain: 'study',
        roles: [],
        userId: 'test-user',
      },
    });

    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();

    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);
    console.log(`  Tools called: ${toolNames.join(', ')}`);

    // Expect KB tools to be used since complaints data is now in session KB
    const usedKbTool = toolNames.some((t) => t.startsWith('kb_'));
    expect(usedKbTool).toBeTruthy();

    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    const response = getAssistantResponse(events);
    expect(response.length).toBeGreaterThan(10);
    console.log(`  Max complaints response (${response.length} chars): ${response.substring(0, 400)}`);
  });

  // ── Step 3: KB query — min complaints ──────────────────────────────────

  test('Step 3: query KB for min complaints', async ({ request }) => {
    const events = await streamChat(request, {
      message: 'Which site or category has the minimum number of complaints?',
      conversationId,
      context: {
        domain: 'study',
        roles: [],
        userId: 'test-user',
      },
    });

    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();

    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);
    console.log(`  Tools called: ${toolNames.join(', ')}`);

    const usedKbTool = toolNames.some((t) => t.startsWith('kb_'));
    expect(usedKbTool).toBeTruthy();

    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    const response = getAssistantResponse(events);
    expect(response.length).toBeGreaterThan(10);
    console.log(`  Min complaints response (${response.length} chars): ${response.substring(0, 400)}`);
  });

  // ── Step 4: Domain switch — study Veritas detailed analysis ────────────

  test('Step 4: switch to study domain, select Veritas, get analysis', async ({ request }) => {
    test.setTimeout(120_000);

    const chatBody: ChatRequest = {
      message: 'Get detailed analysis for study Veritas',
      conversationId,
      context: {
        domain: 'study',
        roles: [],
        userId: 'test-user',
      },
    };

    const events = await streamChat(request, chatBody);

    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();

    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);
    console.log(`  Tools called: ${toolNames.join(', ')}`);

    // Check if user selection is required (multiple Veritas matches)
    const inputRequired = findEvent(events, 'user_input_required');

    if (inputRequired) {
      console.log('  User selection required — selecting first option (CRD_1064 VERITAS)');

      // Select first option and resume
      const resumedEvents = await selectAndResume(request, events, 0, chatBody);

      const resumedDone = findEvent(resumedEvents, 'done');
      expect(resumedDone).toBeDefined();

      const resumedError = findEvent(resumedEvents, 'error');
      expect(resumedError).toBeUndefined();

      const response = getAssistantResponse(resumedEvents);
      expect(response.length).toBeGreaterThan(10);

      // Should contain study analysis content
      const lower = response.toLowerCase();
      const hasStudyContent =
        lower.includes('veritas') ||
        lower.includes('enrollment') ||
        lower.includes('compliance') ||
        lower.includes('study');
      expect(hasStudyContent).toBeTruthy();

      console.log(`  Study analysis (${response.length} chars): ${response.substring(0, 400)}`);
    } else {
      // No selection needed — direct response
      const doneEvent = findEvent(events, 'done');
      expect(doneEvent).toBeDefined();

      const response = getAssistantResponse(events);
      expect(response.length).toBeGreaterThan(10);
      console.log(`  Study analysis (${response.length} chars): ${response.substring(0, 400)}`);
    }
  });
});
