/**
 * Shared helpers for AVA integration tests.
 *
 * Provides:
 *  - SSE stream reader for the /api/chat endpoint
 *  - Typed event interfaces matching the backend StreamEvent contract
 */

import { APIRequestContext } from '@playwright/test';

// ── Types ────────────────────────────────────────────────────────────────────

export interface StreamEvent {
  type: string;
  data: unknown;
}

export interface ChatRequest {
  message: string;
  conversationId: string;
  context: {
    domain: string;
    roles: string[];
    userId: string;
  };
  resumeRunId?: string;
}

export interface SelectRequest {
  agentRunId: string;
  inputType: string;
  selectedIndex?: number;
  confirmedData?: Record<string, unknown>;
}

// ── SSE helpers ──────────────────────────────────────────────────────────────

/**
 * Sends a chat message via POST /api/chat and collects all SSE events
 * until the stream closes.  Returns the full list of parsed StreamEvents.
 */
export async function streamChat(
  request: APIRequestContext,
  body: ChatRequest,
): Promise<StreamEvent[]> {
  const response = await request.fetch('/api/chat', {
    method: 'POST',
    data: body,
    headers: { Accept: 'text/event-stream' },
  });

  if (!response.ok()) {
    const text = await response.text();
    throw new Error(`Chat request failed ${response.status()}: ${text}`);
  }

  const raw = await response.text();
  return parseSSE(raw);
}

/**
 * Parses raw SSE text into StreamEvent objects.
 * Each SSE frame is: `data: <json>\n\n`
 */
export function parseSSE(raw: string): StreamEvent[] {
  const events: StreamEvent[] = [];
  // Split on double-newline boundaries
  const frames = raw.split(/\n\n/).filter(Boolean);
  for (const frame of frames) {
    for (const line of frame.split('\n')) {
      if (line.startsWith('data: ')) {
        try {
          const json = JSON.parse(line.slice(6));
          events.push(json as StreamEvent);
        } catch {
          // non-JSON data line — skip
        }
      }
    }
  }
  return events;
}

/**
 * Extracts all token data from SSE events and concatenates them to get the
 * full assistant response text.  Each token event contains an individual
 * fragment (not an accumulated string).
 */
export function getAssistantResponse(events: StreamEvent[]): string {
  const tokenEvents = events.filter(e => e.type === 'token');
  if (tokenEvents.length === 0) return '';
  return tokenEvents.map(e => e.data as string).join('');
}

/**
 * Finds an event by type.
 */
export function findEvent(events: StreamEvent[], type: string): StreamEvent | undefined {
  return events.find(e => e.type === type);
}

/**
 * Finds all events of a given type.
 */
export function findEvents(events: StreamEvent[], type: string): StreamEvent[] {
  return events.filter(e => e.type === type);
}

/**
 * Helper to create a new conversation ID.
 */
export function newConversationId(): string {
  return crypto.randomUUID();
}

/**
 * Sends a selection response to the /api/chat/select endpoint and then
 * resumes the chat stream with the returned resumeRunId.
 * Returns the SSE events from the resumed stream.
 */
export async function selectAndResume(
  request: APIRequestContext,
  events: StreamEvent[],
  selectedIndex: number,
  chatBody: ChatRequest,
): Promise<StreamEvent[]> {
  const inputEvent = findEvent(events, 'user_input_required');
  if (!inputEvent) throw new Error('No user_input_required event found');

  const payload = inputEvent.data as {
    agentRunId: string;
    inputType: string;
    payload: { options: { label: string; value: string }[] };
  };

  // POST selection
  const selectResponse = await request.fetch('/api/chat/select', {
    method: 'POST',
    data: {
      agentRunId: payload.agentRunId,
      inputType: payload.inputType,
      selectedIndex,
    } as SelectRequest,
  });

  if (!selectResponse.ok()) {
    const text = await selectResponse.text();
    throw new Error(`Select request failed ${selectResponse.status()}: ${text}`);
  }

  // The done event should contain a resumeRunId
  const doneEvent = findEvent(events, 'done');
  const resumeRunId = (doneEvent?.data as { resumeRunId?: string })?.resumeRunId;
  if (!resumeRunId) throw new Error('No resumeRunId in done event');

  // Resume the stream
  return streamChat(request, {
    ...chatBody,
    resumeRunId,
  });
}

// ── Service base URLs ────────────────────────────────────────────────────────

export const ORCHESTRATOR_URL = 'http://localhost:5010';
export const ADO_MCP_URL = 'http://localhost:5020';
export const STUDY_API_URL = 'http://localhost:5030';
