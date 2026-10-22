/**
 * Integration tests: Create RDL for study CRD_1001
 *
 * Tests both:
 *  1. Direct Study API endpoint (POST /api/rdl)
 *  2. Chat endpoint via natural language through the orchestrator
 *
 * Note: The create-rdl tool requires confirmation in the chat flow.
 *       The chat test verifies the agent plans the RDL creation and
 *       either requests confirmation or proceeds to call the tool.
 */

import { test, expect } from '@playwright/test';
import {
  streamChat,
  findEvent,
  findEvents,
  getAssistantResponse,
  newConversationId,
  STUDY_API_URL,
} from './helpers';

// ── Direct endpoint test ─────────────────────────────────────────────────────

test.describe('Create RDL - Direct Study API Endpoint', () => {
  test('POST /api/rdl creates an RDL for study CRD_1001', async ({ request }) => {
    const response = await request.post(`${STUDY_API_URL}/api/rdl`, {
      data: {
        study: 'CRD_1001',
      },
    });

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    expect(body).toHaveProperty('success');
    // Even if the study doesn't exist in the test env, the API should
    // return a structured response
    if (body.success) {
      expect(body).toHaveProperty('studyCode');
      expect(body).toHaveProperty('clinicalStudyId');
      console.log(`  ✓ RDL created successfully for ${body.studyCode} (ID: ${body.clinicalStudyId})`);
      console.log(`    Message: ${body.message}`);
    } else {
      // If study not found, the API returns success=false with error
      expect(body).toHaveProperty('error');
      console.log(`  ✓ API responded correctly: ${body.error ?? body.message}`);
    }
  });

  test('POST /api/rdl with activeFg and blindedFg flags', async ({ request }) => {
    const response = await request.post(`${STUDY_API_URL}/api/rdl`, {
      data: {
        study: 'CRD_1001',
        activeFg: 'Y',
        blindedFg: 'N',
      },
    });

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    expect(body).toHaveProperty('success');
    console.log(`  ✓ RDL request with flags: success=${body.success}`);
    if (body.message) console.log(`    Message: ${body.message}`);
  });
});

// ── Chat endpoint test ───────────────────────────────────────────────────────

test.describe('Create RDL - Chat Endpoint', () => {
  test('natural language request to create RDL via orchestrator', async ({ request }) => {
    const conversationId = newConversationId();

    const events = await streamChat(request, {
      message: 'Create RDL for study CRD_1001',
      conversationId,
      context: {
        domain: 'study',
        roles: [],
        userId: 'test-user',
      },
    });

    // Should have started an agent run
    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();
    expect(runStarted!.data).toHaveProperty('agentRunId');

    // The orchestrator may call create-rdl directly, use start_workflow,
    // or request confirmation first. All are valid execution paths.
    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);
    const confirmationRequired = findEvent(events, 'confirmation_required');

    console.log(`  ✓ Agent called tools: ${toolNames.join(', ') || '(none)'}`);

    const rdlToolCalled = toolNames.includes('create-rdl');
    const hasConfirmation = confirmationRequired !== undefined;
    const usedWorkflow = toolNames.includes('start_workflow');
    const studyToolCalled = toolNames.some(
      (t) => t === 'study-find' || t === 'study-search'
    );

    expect(rdlToolCalled || hasConfirmation || studyToolCalled || usedWorkflow).toBeTruthy();

    if (hasConfirmation) {
      console.log('  ✓ Agent requested confirmation before creating RDL (expected for create-rdl tool)');
    }

    if (rdlToolCalled) {
      // Check tool result
      const toolResults = findEvents(events, 'tool_result');
      const rdlResult = toolResults.find(
        (e) => (e.data as { tool: string }).tool === 'create-rdl'
      );
      if (rdlResult) {
        console.log(`  ✓ RDL tool returned result`);
      }
    }

    // Should complete (done or paused for confirmation)
    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    const response = getAssistantResponse(events);
    expect(response.length).toBeGreaterThan(0);
    console.log(`  ✓ Assistant response (${response.length} chars): ${response.substring(0, 200)}...`);
  });
});
