import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { ModuleKey } from './services/security.service';

export const routes: Routes = [
  { path: '', redirectTo: 'domains', pathMatch: 'full' },
  {
    path: 'workflow-builder',
    canActivate: [authGuard],
    data: { moduleKey: ModuleKey.WORKFLOW_BUILDER },
    loadComponent: () => import('./pages/workflow-builder/workflow-builder').then(m => m.WorkflowBuilder)
  },
  {
    path: 'data-templates',
    canActivate: [authGuard],
    data: { moduleKey: ModuleKey.DATA_TEMPLATES },
    loadComponent: () => import('./pages/data-templates/data-templates').then(m => m.DataTemplates)
  },
  {
    path: 'domains',
    canActivate: [authGuard],
    data: { moduleKey: ModuleKey.DOMAIN_ADMIN },
    loadComponent: () => import('./pages/domains/domains').then(m => m.Domains)
  },
  {
    path: 'tools',
    canActivate: [authGuard],
    data: { moduleKey: ModuleKey.TOOL_MANAGER },
    loadComponent: () => import('./pages/tool-server-manager/tool-server-manager').then(m => m.ToolServerManager)
  },
  {
    path: 'not-authorized',
    loadComponent: () => import('./pages/not-authorized/not-authorized').then(m => m.NotAuthorized)
  },
  // Legacy redirect
  { path: 'domain-files', redirectTo: 'domains', pathMatch: 'full' },
  { path: '**', redirectTo: 'domains' },
];
