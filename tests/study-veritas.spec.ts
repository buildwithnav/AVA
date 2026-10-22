/**
 * Integration tests: Get detailed analysis for study "Veritas"
 *
 * Tests both:
 *  1. Direct Study API endpoints (search + details)
 *  2. Chat endpoint via natural language through the orchestrator
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

// ── Direct endpoint tests ────────────────────────────────────────────────────

test.describe('Study Veritas Analysis - Direct Study API Endpoints', () => {
  test('GET /api/studies/find resolves study Veritas', async ({ request }) => {
    const response = await request.get(`${STUDY_API_URL}/api/studies/find`, {
      params: { q: 'veritas' },
    });

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    // The find endpoint returns a study with 'value' as ID and studyName/studyCode fields
    expect(body).toHaveProperty('value');
    expect(body).toHaveProperty('studyName');
    expect(body).toHaveProperty('studyCode');
    expect(body.studyName.toLowerCase()).toContain('veritas');
    console.log(`  ✓ Found study: ${body.studyCode} - ${body.studyName}`);
    console.log(`    Study ID (value): ${body.value}`);
  });

  test('GET /api/studies?search=veritas lists matching studies', async ({ request }) => {
    const response = await request.get(`${STUDY_API_URL}/api/studies`, {
      params: { search: 'veritas', top: '5' },
    });

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    // Response shape: { count: N, studies: [...] }
    expect(body).toHaveProperty('count');
    expect(body).toHaveProperty('studies');
    expect(Array.isArray(body.studies)).toBeTruthy();
    expect(body.studies.length).toBeGreaterThan(0);

    const match = body.studies.find(
      (s: any) =>
        (s.studyName ?? '').toLowerCase().includes('veritas') ||
        (s.studyCode ?? '').toLowerCase().includes('veritas')
    );
    expect(match).toBeDefined();
    console.log(`  ✓ Search returned ${body.count} result(s), matched: ${match.studyCode} - ${match.studyName}`);
  });

  test('GET /api/studies/{id}/details returns detailed analysis', async ({ request }) => {
    // First resolve the study ID
    const findResp = await request.get(`${STUDY_API_URL}/api/studies/find`, {
      params: { q: 'veritas' },
    });
    expect(findResp.ok()).toBeTruthy();
    const found = await findResp.json();
    const id = found.value;

    const response = await request.get(`${STUDY_API_URL}/api/studies/${id}/details`);

    expect(response.ok()).toBeTruthy();

    const body = await response.json();
    expect(body).toHaveProperty('studyId');
    expect(body).toHaveProperty('studyCode');
    expect(body).toHaveProperty('studyName');
    expect(body).toHaveProperty('actualEnrollment');
    expect(body).toHaveProperty('followUpCompliance');
    console.log(`  ✓ Got detailed analysis for study ${body.studyCode} (ID: ${body.studyId})`);
    console.log(`    Enrollment: ${body.actualEnrollment}/${body.sampleSize}, Compliance: ${body.followUpCompliance}%`);
  });
});

// ── Chat endpoint test ───────────────────────────────────────────────────────

test.describe('Study Veritas Analysis - Chat Endpoint', () => {
  test('natural language request for detailed study analysis via orchestrator', async ({ request }) => {
    const conversationId = newConversationId();

    const events = await streamChat(request, {
      message: 'Get detailed analysis for study Veritas',
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

    // The orchestrator may use start_workflow (for multi-step flows) or
    // call study tools directly. Both are valid.
    const toolCalls = findEvents(events, 'tool_call');
    const toolNames = toolCalls.map((e) => (e.data as { tool: string }).tool);

    console.log(`  ✓ Agent called tools: ${toolNames.join(', ') || '(none)'}`);

    // Accept either direct tool calls or workflow-based execution
    const usedWorkflow = toolNames.includes('start_workflow');
    const usedStudyTools = toolNames.some(
      (t) => t === 'study-find' || t === 'study-search' || t === 'study-details'
    );
    expect(usedWorkflow || usedStudyTools).toBeTruthy();

    // Should complete without error
    const doneEvent = findEvent(events, 'done');
    expect(doneEvent).toBeDefined();

    const errorEvent = findEvent(events, 'error');
    expect(errorEvent).toBeUndefined();

    // The response may come as tokens (from ai-generate/ai-analyze stages)
    // or as a user_input_required event (selection prompt when multiple matches).
    // Either constitutes a valid response.
    const response = getAssistantResponse(events);
    const inputRequired = findEvent(events, 'user_input_required');
    expect(response.length > 0 || inputRequired).toBeTruthy();
    console.log(`  ✓ Assistant response (${response.length} chars): ${response.substring(0, 200)}...`);
    if (inputRequired) {
      console.log('  ✓ User selection required (multiple study matches)');
    }
  });
});
