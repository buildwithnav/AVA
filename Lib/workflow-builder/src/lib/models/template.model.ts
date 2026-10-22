// ─── Template Definition ────────────────────────────────────────
// Mirrors ava-api TemplateDefinition and templates.json schema.

export type FieldType = 'text' | 'multiline' | 'select' | 'number' | 'date';

export interface TemplateField {
  name: string;
  label: string;
  type: FieldType;
  required: boolean;
  options?: string[];
  placeholder?: string;
}

export interface TemplateDefinition {
  id: string;
  description: string;
  fields: TemplateField[];
}

/** Factory for creating an empty template */
export function createEmptyTemplate(): TemplateDefinition {
  return {
    id: '',
    description: '',
    fields: [],
  };
}

/** Factory for creating an empty template field */
export function createEmptyField(): TemplateField {
  return {
    name: '',
    label: '',
    type: 'text',
    required: false,
  };
}
