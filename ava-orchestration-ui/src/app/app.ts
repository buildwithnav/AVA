import { Component } from '@angular/core';
import { Shell } from './components/shell/shell';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [Shell],
  template: `<app-shell></app-shell>`,
  styles: [`:host { display: flex; flex-direction: column; height: 100vh; }`]
})
export class App {}
