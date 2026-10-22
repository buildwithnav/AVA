import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { PlanStep, StepExecution } from '../../models/chat.models';

@Component({
  selector: 'app-agent-run-panel',
  standalone: true,
  imports: [
    CommonModule,
    MatExpansionModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    MatIconModule,
  ],
  templateUrl: './agent-run-panel.html',
  styleUrl: './agent-run-panel.scss',
})
export class AgentRunPanel {
  @Input() plan: PlanStep[] = [];
  @Input() steps: StepExecution[] = [];
  @Input() isRunning = false;

  getStepExecution(stepId: string): StepExecution | undefined {
    return this.steps.find(s => s.stepId === stepId);
  }

  getStepStatus(stepId: string): 'pending' | 'running' | 'success' | 'failed' {
    const exec = this.getStepExecution(stepId);
    if (!exec) {
      // Check if this is the currently executing step
      const currentIndex = this.plan.findIndex(p => !this.getStepExecution(p.stepId));
      const stepIndex = this.plan.findIndex(p => p.stepId === stepId);
      return this.isRunning && stepIndex === currentIndex ? 'running' : 'pending';
    }
    return exec.success ? 'success' : 'failed';
  }
}
