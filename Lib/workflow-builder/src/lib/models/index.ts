// Barrel export for all models
export * from './prompt.model';
export * from './tool.model';
export * from './template.model';
export * from './toolbox.model';

/** A domain entry with its short ID and human-readable display name */
export interface DomainInfo {
  id: string;
  name: string;
}
