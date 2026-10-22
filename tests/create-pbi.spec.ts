/**
 * Integration tests: Create a PBI under epic "Equinox - Sync Validation"
 * in feature "Equinox enhancements".
 *
 * Tests both:
 *  1. Direct ADO MCP endpoint (POST /api/workitems/pbi)
 *  2. Chat endpoint via natural language through the orchestrator
 */

import { test, expect } from '@playwright/test';
import {
  streamChat,
  findEvent,
  findEvents,
  getAssistantResponse,
  newConversationId,
  ADO_MCP_URL,
} from './helpers';

// ── Direct endpoint test ─────────────────────────────────────────────────────

test.describe('Create PBI - Direct ADO MCP Endpoint', () => {
  test('POST /api/workitems/pbi creates a PBI under epic and feature', async ({ request }) => {
    const response = await request.post(`${ADO_MCP_URL}/api/workitems/pbi`, {
      data: {
        title: 'Investigate API slowness in sync validation flow',
        description:
          '<p>This PBI tracks the investigation and resolution of API slowness ' +
          'observed during the sync validation process in the Equinox platform.</p>',
        epicTitle: 'Equinox - Sync Validation',
        featureTitle: 'Equinox enhancements',
        priority: 2,
        acceptanceCriteria:
          '<ul>' +
          '<li>API response times are profiled and documented</li>' +
          '<li>Root cause of slowness is identified</li>' +
          '<li>Fix is implemented and sync validation completes within SLA</li>' +
          '</ul>',
      },
    });

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    expect(body).toHaveProperty('id');
    expect(body.id).toBeGreaterThan(0);
    expect(body).toHaveProperty('title');
    expect(body.title).toContain('slowness');
    expect(body).toHaveProperty('state', 'New');
    // Should be linked to a parent (epic or feature)
    expect(body).toHaveProperty('message');
    console.log(`  ✓ Created PBI #${body.id}: ${body.title}`);
    console.log(`    Parent: ${body.parentId ?? 'none'}`);
    console.log(`    Message: ${body.message}`);
  });
});

// ── Chat endpoint test ───────────────────────────────────────────────────────

test.describe('Create PBI - Chat Endpoint', () => {
  test('natural language request to create PBI via orchestrator', async ({ request }) => {
    const conversationId = newConversationId();

    const events = await streamChat(request, {
      message:
        'Create a PBI under epic "Equinox - Sync Validation" in feature "Equinox enhancements". ' +
        'Title it "Investigate API slowness in sync validation flow". ' +
        'This feature is about the slowness of APIs. Priority 2.',
      conversationId,
      context: {
        domain: 'ado',
        roles: [],
        userId: 'test-user',
      },
    });

    // Should have started an agent run
    const runStarted = findEvent(events, 'run_started');
    expect(runStarted).toBeDefined();
    expect(runStarted!.data).toHaveProperty('agentRunId');

    // The orchestrator may call ado-create-pbi directly or use start_workflow
    // for multi-step flows. Both are valid execution paths.
    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);

    console.log(`  ✓ Agent called tools: ${toolNames.join(', ') || '(none)'}`);

    const usedPbiTool = toolNames.includes('ado-create-pbi');
    const usedWorkflow = toolNames.includes('start_workflow');
    expect(usedPbiTool || usedWorkflow).toBeTruthy();

    // Should have completed successfully (done event, no error)
    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    // The assistant response should mention the PBI creation
    const response = getAssistantResponse(events);
    expect(response.length).toBeGreaterThan(0);
    console.log(`  ✓ Assistant response (${response.length} chars): ${response.substring(0, 200)}...`);
  });
});
