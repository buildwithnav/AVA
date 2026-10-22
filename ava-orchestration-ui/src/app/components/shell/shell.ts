import { Component, signal, OnInit, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, ActivatedRoute } from '@angular/router';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { SecurityService, ModuleKey, ModuleAccess } from '../../services/security.service';

interface NavItem {
  label: string;
  icon: string;
  route: string;
  moduleKey: string;
}

/**
 * Nav items + the module key each requires, matching app.routes.ts's route data. Flat nav
 * (4 items, no sub-links) — a small array here is simpler than a separate route-config file.
 */
const NAV_ITEMS: NavItem[] = [
  { label: 'Domains',        icon: 'public',      route: '/domains',           moduleKey: ModuleKey.DOMAIN_ADMIN },
  { label: 'Workflows',      icon: 'schema',       route: '/workflow-builder', moduleKey: ModuleKey.WORKFLOW_BUILDER },
  { label: 'Data Templates', icon: 'description',  route: '/data-templates',   moduleKey: ModuleKey.DATA_TEMPLATES },
  { label: 'Tools',          icon: 'build',        route: '/tools',            moduleKey: ModuleKey.TOOL_MANAGER },
];

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatSidenavModule,
    MatToolbarModule,
    MatListModule,
    MatIconModule,
    MatButtonModule,
  ],
  host: { style: 'display:block;height:100%' },
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit {
  /** When true, hides sidenav + toolbar for iframe embedding */
  embedMode = signal(false);

  private security = inject(SecurityService);

  navItems: NavItem[] = [];

  constructor(private route: ActivatedRoute) {}

  ngOnInit(): void {
    this.navItems = NAV_ITEMS.filter(item =>
      this.security.getModuleAccess(item.moduleKey, ModuleAccess.BROWSE),
    );

    // Check both query params and URL for embed flag (iframe from ava-loader)
    this.route.queryParams.subscribe(params => {
      if (params['embed'] === 'true') {
        this.embedMode.set(true);
      }
    });
    // Fallback: check window.location for embed=true (in case route doesn't capture it)
    if (window.location.search.includes('embed=true')) {
      this.embedMode.set(true);
    }
  }
}
