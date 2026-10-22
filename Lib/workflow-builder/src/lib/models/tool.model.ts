// ─── Tool Definition ────────────────────────────────────────────
// Mirrors ava-api ToolDefinition and tools.json schema.

export interface ToolParameter {
  name: string;
  type: 'string' | 'number' | 'boolean';
  required: boolean;
  location: 'body' | 'query' | 'path';
  description?: string;
}

export interface StoreAsKBConfig {
  enabled: boolean;
  scope: 'session' | 'domain';
  kbName?: string;
  type?: 'dataset' | 'document';
}

export interface ToolDefinition {
  id: string;
  service: string;
  route: string;
  httpMethod: string;
  description?: string;
  parameters: ToolParameter[];
  requiresConfirmation: boolean;
  internal: boolean;
  responseFormat?: 'json' | 'csv' | 'xlsx';
  storeAsKB?: StoreAsKBConfig;
}

/** Grouped view of tools by service (for the tool picker) */
export interface ToolServiceGroup {
  service: string;
  tools: ToolDefinition[];
}

/** Group a flat tool list by service */
export function groupToolsByService(tools: ToolDefinition[]): ToolServiceGroup[] {
  const map = new Map<string, ToolDefinition[]>();
  for (const tool of tools) {
    const group = map.get(tool.service) ?? [];
    group.push(tool);
    map.set(tool.service, group);
  }
  return Array.from(map.entries()).map(([service, tools]) => ({ service, tools }));
}
