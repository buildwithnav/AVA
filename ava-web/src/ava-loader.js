/**
 * AVA Loader — Drop-in companion sidebar for internal apps.
 *
 * Usage (floating FAB — default):
 *   <script src="https://ava.company.com/ava-loader.js"
 *           data-app="ClaimsPortal"></script>
 *
 * Usage (injected into existing header):
 *   <script src="https://ava.company.com/ava-loader.js"
 *           data-app="CMA"
 *           data-target=".site-header .action-item-container"></script>
 *
 * Optional attributes:
 *   data-api-url   Override the API base URL (default: derived from loader origin)
 *   data-admin-url Override the orchestration UI URL (default: derived from loader origin)
 *   data-target    CSS selector for an element to inject the toggle button into
 *                  (inserts as the first child). Falls back to floating FAB if
 *                  the selector doesn't match anything.
 */
(function () {
  'use strict';

  // ── Configuration ────────────────────────────────────────────────────────
  var script = document.currentScript;
  if (!script) return;

  var baseUrl = script.src.substring(0, script.src.lastIndexOf('/') + 1);
  var appContext = script.getAttribute('data-app') || 'default';
  var apiUrl = script.getAttribute('data-api-url') || (function () {
    try {
      var u = new URL(script.src);
      return u.origin.replace('://ava.', '://ava-api.');
    } catch (e) {
      return '';
    }
  })();
  var targetSelector = script.getAttribute('data-target') || '';

  var STORAGE_KEY = 'ava-panel-width';
  var DEFAULT_WIDTH = 600;
  var MIN_WIDTH = 600;

  var isOpen = false;
  var isLoaded = false;
  var panel = null;
  var toggleBtn = null;
  var handle = null;
  var mode = ''; // 'header' or 'fab'
  var currentView = 'chat'; // 'chat' | 'workflows'
  var workflowFrame = null;
  var panelTitle = null;
  var workflowMenuItem = null;
  var adminUrl = script.getAttribute('data-admin-url') || (function () {
    try {
      var u = new URL(script.src);
      if (u.hostname === 'localhost' || u.hostname === '127.0.0.1') {
        return u.protocol + '//' + u.hostname + ':6201';
      }
      return u.origin.replace('://ava.', '://ava-admin.');
    } catch (e) {
      return 'http://localhost:6201';
    }
  })();

  var AVA_ICON_SVG =
    '<span class="fas fa-robot" style="font-size:18px"></span>';

  // ── Theme Definitions ────────────────────────────────────────────────────
  var AVA_THEMES = [
    { id: 'ocean',    name: 'Ocean Blue',  swatch: 'linear-gradient(135deg, #0d47a1, #42a5f5)', header: 'linear-gradient(135deg, #0d47a1, #1976d2)' },
    { id: 'midnight', name: 'Midnight',    swatch: 'linear-gradient(135deg, #1e1b4b, #6366f1)', header: 'linear-gradient(135deg, #1e1b4b, #4338ca)' },
    { id: 'forest',   name: 'Forest',      swatch: 'linear-gradient(135deg, #1b5e20, #66bb6a)', header: 'linear-gradient(135deg, #1b5e20, #388e3c)' },
    { id: 'sunset',   name: 'Sunset',      swatch: 'linear-gradient(135deg, #bf360c, #ffb74d)', header: 'linear-gradient(135deg, #bf360c, #e64a19)' },
    { id: 'rose',     name: 'Rosé',        swatch: 'linear-gradient(135deg, #880e4f, #ec407a)', header: 'linear-gradient(135deg, #880e4f, #c2185b)' },
  ];
  var THEME_STORAGE_KEY = 'ava-theme';

  // ── Inject Required Fonts ────────────────────────────────────────────────
  function injectFonts() {
    // AVA's theme (--ava-font / mat.theme typography) uses Inter, not Roboto — must match.
    if (!document.querySelector('link[href*="fonts.googleapis.com/css2"][href*="Inter"]')) {
      var link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = 'https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&display=swap';
      document.head.appendChild(link);
    }
    if (!document.querySelector('link[href*="font-awesome"], link[href*="fontawesome"]')) {
      var link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = 'https://pro.fontawesome.com/releases/v5.10.0/css/all.css';
      link.crossOrigin = 'anonymous';
      document.head.appendChild(link);
    }
    if (!document.querySelector('link[href*="fonts.googleapis.com"][href*="Material+Icons"]')) {
      var link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = 'https://fonts.googleapis.com/icon?family=Material+Icons';
      document.head.appendChild(link);
    }
  }

  // ── Inject Styles ────────────────────────────────────────────────────────
  function injectStyles() {
    var css = [
      '/* ── FAB mode ─────────────────────────────────────────────────────── */',
      '#ava-fab {',
      '  position: fixed;',
      '  bottom: 24px;',
      '  right: 24px;',
      '  width: 56px;',
      '  height: 56px;',
      '  border-radius: 50%;',
      '  background: #1976d2;',
      '  color: #fff;',
      '  border: none;',
      '  cursor: pointer;',
      '  box-shadow: 0 4px 12px rgba(0,0,0,0.3);',
      '  z-index: 10001;',
      '  font-size: 24px;',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: center;',
      '  transition: transform 0.2s ease, background-color 0.2s ease;',
      '  outline: none;',
      '}',
      '#ava-fab:hover { transform: scale(1.1); background: #1565c0; }',
      '#ava-fab:focus-visible { box-shadow: 0 0 0 3px rgba(25,118,210,0.5); }',
      '#ava-fab.ava-fab-active { background: #424242; }',
      '',
      '/* ── Header-button mode ────────────────────────────────────────────── */',
      '#ava-header-btn {',
      '  display: inline-flex;',
      '  align-items: center;',
      '  gap: 6px;',
      '  background: none;',
      '  border: none;',
      '  cursor: pointer;',
      '  color: inherit;',
      '  padding: 0 8px;',
      '  height: 40px;',
      '  border-radius: 4px;',
      '  font-family: inherit;',
      '  font-size: 14px;',
      '  transition: background 0.15s ease;',
      '}',
      '#ava-header-btn:hover { background: rgba(255,255,255,0.15); }',
      '#ava-header-btn:focus-visible { outline: 2px solid rgba(255,255,255,0.6); outline-offset: 2px; }',
      '#ava-header-btn.ava-active { background: rgba(255,255,255,0.25); }',
      '#ava-header-btn .ava-btn-label { font-weight: 500; }',
      '',
      '/* ── Panel (shared) ────────────────────────────────────────────────── */',
      '#ava-panel {',
      '  position: fixed;',
      '  top: 0;',
      '  right: 0;',
      '  bottom: 0;',
      '  background: #fff;',
      '  box-shadow: -2px 0 16px rgba(0,0,0,0.15);',
      '  z-index: 10000;',
      '  transform: translateX(100%);',
      '  transition: transform 0.3s cubic-bezier(0.4, 0, 0.2, 1);',
      '  display: flex;',
      '  flex-direction: column;',
      '  overflow: hidden;',
      '}',
      '#ava-panel.ava-panel-open { transform: translateX(0); }',
      '',
      '#ava-drag-handle {',
      '  position: absolute;',
      '  top: 0;',
      '  left: 0;',
      '  width: 6px;',
      '  height: 100%;',
      '  cursor: col-resize;',
      '  background: transparent;',
      '  z-index: 1;',
      '  transition: background 0.15s ease;',
      '}',
      '#ava-drag-handle:hover, #ava-drag-handle.ava-dragging {',
      '  background: rgba(25, 118, 210, 0.3);',
      '}',
      '#ava-panel.ava-resizing iframe { pointer-events: none; }',
      '',
      '#ava-panel ava-chat {',
      '  flex: 1;',
      '  min-height: 0;',
      '  display: flex;',
      '  flex-direction: column;',
      '}',
      '',
      '#ava-panel-header {',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: space-between;',
      '  padding: 8px 12px;',
      '  background: #1976d2;',
      '  color: #fff;',
      '  font-family: Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;',
      '  font-size: 16px;',
      '  font-weight: 600;',
      '  letter-spacing: 0.05em;',
      '  flex-shrink: 0;',
      '}',
      '#ava-panel-close {',
      '  background: none;',
      '  border: none;',
      '  color: rgba(255,255,255,0.85);',
      '  cursor: pointer;',
      '  font-size: 18px;',
      '  width: 32px;',
      '  height: 32px;',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: center;',
      '  border-radius: 4px;',
      '  transition: background 0.15s ease;',
      '}',
      '#ava-panel-close:hover { background: rgba(255,255,255,0.2); }',
      '',
      '#ava-new-chat-btn {',
      '  background: none;',
      '  border: none;',
      '  color: rgba(255,255,255,0.85);',
      '  cursor: pointer;',
      '  font-size: 16px;',
      '  width: 32px;',
      '  height: 32px;',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: center;',
      '  border-radius: 4px;',
      '  transition: background 0.15s ease;',
      '  margin-left: auto;',
      '}',
      '#ava-new-chat-btn:hover { background: rgba(255,255,255,0.2); }',
      '',
      '/* ── Menu button & dropdown ────────────────────────────────────────── */',
      '#ava-menu-btn {',
      '  background: none;',
      '  border: none;',
      '  color: rgba(255,255,255,0.85);',
      '  cursor: pointer;',
      '  font-size: 18px;',
      '  width: 32px;',
      '  height: 32px;',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: center;',
      '  border-radius: 4px;',
      '  transition: background 0.15s ease;',
      '}',
      '#ava-menu-btn:hover { background: rgba(255,255,255,0.2); }',
      '',
      '#ava-menu-dropdown {',
      '  display: none;',
      '  position: absolute;',
      '  top: 100%;',
      '  left: 8px;',
      '  margin-top: 4px;',
      '  background: #fff;',
      '  border-radius: 8px;',
      '  box-shadow: 0 4px 16px rgba(0,0,0,0.18);',
      '  min-width: 180px;',
      '  z-index: 10002;',
      '  padding: 8px 0;',
      '  font-family: Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;',
      '  font-size: 14px;',
      '  color: #333;',
      '}',
      '#ava-menu-dropdown.ava-menu-open { display: block; }',
      '',
      '.ava-menu-section {',
      '  padding: 8px 12px;',
      '}',
      '.ava-menu-section-label {',
      '  font-size: 11px;',
      '  font-weight: 600;',
      '  text-transform: uppercase;',
      '  letter-spacing: 0.05em;',
      '  color: #888;',
      '  margin-bottom: 6px;',
      '}',
      '.ava-menu-swatches {',
      '  display: flex;',
      '  gap: 8px;',
      '}',
      '.ava-menu-swatch {',
      '  width: 24px;',
      '  height: 24px;',
      '  border-radius: 50%;',
      '  border: 2px solid transparent;',
      '  cursor: pointer;',
      '  transition: transform 0.15s ease, border-color 0.15s ease;',
      '}',
      '.ava-menu-swatch:hover { transform: scale(1.2); }',
      '.ava-menu-swatch.ava-swatch-active { border-color: #1976d2; transform: scale(1.15); }',
      '',
      '.ava-menu-divider {',
      '  height: 1px;',
      '  background: #e0e0e0;',
      '  margin: 4px 0;',
      '}',
      '.ava-menu-item {',
      '  display: flex;',
      '  align-items: center;',
      '  gap: 10px;',
      '  padding: 8px 12px;',
      '  cursor: pointer;',
      '  transition: background 0.12s ease;',
      '  border: none;',
      '  background: none;',
      '  width: 100%;',
      '  text-align: left;',
      '  font-family: inherit;',
      '  font-size: 14px;',
      '  color: #333;',
      '}',
      '.ava-menu-item:hover { background: #f5f5f5; }',
      '.ava-menu-item i { width: 18px; text-align: center; color: #666; }',
      '.ava-menu-item .ava-connected-dot {',
      '  width: 8px;',
      '  height: 8px;',
      '  border-radius: 50%;',
      '  background: #4caf50;',
      '  margin-left: auto;',
      '  box-shadow: 0 0 0 0 rgba(76,175,80,0.5);',
      '  animation: ava-pulse 2s ease-in-out infinite;',
      '}',
      '@keyframes ava-pulse {',
      '  0%, 100% { box-shadow: 0 0 0 0 rgba(76,175,80,0.5); }',
      '  50% { box-shadow: 0 0 0 4px rgba(76,175,80,0); }',
      '}',
      '',
      '/* ── Auth Popovers ─────────────────────────────────────────────────── */',
      '.ava-auth-popover {',
      '  position: absolute;',
      '  top: 100%;',
      '  left: 8px;',
      '  right: 8px;',
      '  margin-top: 4px;',
      '  background: #fff;',
      '  border-radius: 8px;',
      '  box-shadow: 0 4px 20px rgba(0,0,0,0.22);',
      '  z-index: 10003;',
      '  padding: 14px;',
      '  font-family: Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;',
      '  font-size: 13px;',
      '  color: #333;',
      '}',
      '.ava-auth-popover-title {',
      '  display: flex;',
      '  align-items: center;',
      '  gap: 8px;',
      '  font-weight: 600;',
      '  font-size: 13px;',
      '  margin-bottom: 10px;',
      '  color: #444;',
      '}',
      '.ava-auth-popover-title i { color: #666; }',
      '.ava-auth-popover-close {',
      '  margin-left: auto;',
      '  background: none;',
      '  border: none;',
      '  cursor: pointer;',
      '  color: #999;',
      '  font-size: 14px;',
      '  padding: 2px 4px;',
      '  border-radius: 4px;',
      '}',
      '.ava-auth-popover-close:hover { background: #f0f0f0; color: #333; }',
      '.ava-auth-row {',
      '  display: flex;',
      '  gap: 8px;',
      '  align-items: center;',
      '}',
      '.ava-auth-row input {',
      '  flex: 1;',
      '  padding: 7px 10px;',
      '  border: 1px solid #ccc;',
      '  border-radius: 6px;',
      '  font-size: 13px;',
      '  font-family: inherit;',
      '  outline: none;',
      '  transition: border-color 0.15s;',
      '}',
      '.ava-auth-row input:focus { border-color: #1976d2; }',
      '.ava-auth-btn {',
      '  padding: 7px 14px;',
      '  border: none;',
      '  border-radius: 6px;',
      '  background: #1976d2;',
      '  color: #fff;',
      '  font-size: 13px;',
      '  font-family: inherit;',
      '  font-weight: 500;',
      '  cursor: pointer;',
      '  white-space: nowrap;',
      '  transition: background 0.15s;',
      '}',
      '.ava-auth-btn:hover { background: #1565c0; }',
      '.ava-auth-btn:disabled { background: #bbb; cursor: default; }',
      '.ava-auth-btn.ava-btn-danger { background: #d32f2f; }',
      '.ava-auth-btn.ava-btn-danger:hover { background: #b71c1c; }',
      '.ava-auth-device-code {',
      '  display: flex;',
      '  flex-direction: column;',
      '  gap: 8px;',
      '  align-items: center;',
      '  text-align: center;',
      '}',
      '.ava-auth-device-code .ava-code {',
      '  font-size: 18px;',
      '  font-weight: 700;',
      '  letter-spacing: 0.1em;',
      '  color: #1976d2;',
      '  padding: 4px 12px;',
      '  background: #e3f2fd;',
      '  border-radius: 6px;',
      '}',
      '.ava-auth-device-code .ava-hint { font-size: 12px; color: #888; }',
      '.ava-auth-connected-msg {',
      '  display: flex;',
      '  align-items: center;',
      '  gap: 8px;',
      '  color: #2e7d32;',
      '  font-weight: 500;',
      '}',
      '.ava-auth-connected-msg i { color: #4caf50; }',
      '',
      '#ava-panel .ava-loading {',
      '  display: flex;',
      '  align-items: center;',
      '  justify-content: center;',
      '  flex: 1;',
      '  color: #888;',
      '  font-family: Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;',
      '  font-size: 14px;',
      '}',
      '',
      '/* ── Workflow iframe ───────────────────────────────────────────────── */',
      '#ava-workflow-frame {',
      '  flex: 1;',
      '  border: none;',
      '  min-height: 0;',
      '  width: 100%;',
      '  display: none;',
      '}',
      '#ava-workflow-frame.ava-view-active {',
      '  display: block;',
      '}',
    ].join('\n');

    var style = document.createElement('style');
    style.id = 'ava-loader-styles';
    style.textContent = css;
    document.head.appendChild(style);
  }

  // ── Toggle Button Creation ───────────────────────────────────────────────
  function createHeaderButton(container) {
    mode = 'header';
    toggleBtn = document.createElement('button');
    toggleBtn.id = 'ava-header-btn';
    toggleBtn.setAttribute('aria-label', 'Toggle AVA assistant');
    toggleBtn.title = 'Open AVA';
    toggleBtn.innerHTML = AVA_ICON_SVG + '<span class="ava-btn-label">AVA</span>';
    toggleBtn.addEventListener('click', toggle);
    container.insertBefore(toggleBtn, container.firstChild);
  }

  function createFab() {
    mode = 'fab';
    toggleBtn = document.createElement('button');
    toggleBtn.id = 'ava-fab';
    toggleBtn.setAttribute('aria-label', 'Toggle AVA assistant');
    toggleBtn.title = 'Open AVA';
    toggleBtn.innerHTML =
      '<span class="fas fa-robot" style="font-size:24px"></span>';
    toggleBtn.addEventListener('click', toggle);
    document.body.appendChild(toggleBtn);
  }

  // ── Panel ────────────────────────────────────────────────────────────────
  function createPanel() {
    panel = document.createElement('div');
    panel.id = 'ava-panel';
    panel.style.width = getSavedWidth() + 'px';

    handle = document.createElement('div');
    handle.id = 'ava-drag-handle';
    handle.setAttribute('aria-label', 'Resize AVA panel');
    panel.appendChild(handle);

    // Panel header with menu, title, and close button
    var header = document.createElement('div');
    header.id = 'ava-panel-header';
    header.style.position = 'relative';

    // Menu button (vertical ellipsis)
    var menuBtn = document.createElement('button');
    menuBtn.id = 'ava-menu-btn';
    menuBtn.setAttribute('aria-label', 'Open menu');
    menuBtn.title = 'Menu';
    menuBtn.innerHTML = '<i class="fas fa-ellipsis-v"></i>';
    header.appendChild(menuBtn);

    // Title
    var titleSpan = document.createElement('span');
    titleSpan.textContent = 'AVA';
    panelTitle = titleSpan;
    header.appendChild(titleSpan);

    // New Chat button
    var newChatBtn = document.createElement('button');
    newChatBtn.id = 'ava-new-chat-btn';
    newChatBtn.setAttribute('aria-label', 'New conversation');
    newChatBtn.title = 'New conversation';
    newChatBtn.innerHTML = '<i class="fas fa-plus"></i>';
    newChatBtn.addEventListener('click', function () {
      var chatEl = panel.querySelector('ava-chat');
      if (chatEl) {
        chatEl.dispatchEvent(new CustomEvent('new-chat', { bubbles: false }));
      }
    });
    header.appendChild(newChatBtn);

    // Close button
    var closeBtn = document.createElement('button');
    closeBtn.id = 'ava-panel-close';
    closeBtn.setAttribute('aria-label', 'Minimize AVA');
    closeBtn.title = 'Minimize AVA. Processing will continue in the background.';
    closeBtn.innerHTML = '<i class="fas fa-chevron-double-right"></i>';
    closeBtn.addEventListener('click', toggle);
    header.appendChild(closeBtn);

    // Dropdown menu
    var dropdown = document.createElement('div');
    dropdown.id = 'ava-menu-dropdown';

    // Theme section
    var themeSection = document.createElement('div');
    themeSection.className = 'ava-menu-section';
    var themeLabel = document.createElement('div');
    themeLabel.className = 'ava-menu-section-label';
    themeLabel.textContent = 'Theme';
    themeSection.appendChild(themeLabel);

    var swatchRow = document.createElement('div');
    swatchRow.className = 'ava-menu-swatches';
    var savedTheme = getThemeCookie() || localStorage.getItem('ava-theme') || 'ocean';
    AVA_THEMES.forEach(function (t) {
      var swatch = document.createElement('div');
      swatch.className = 'ava-menu-swatch' + (t.id === savedTheme ? ' ava-swatch-active' : '');
      swatch.style.background = t.swatch;
      swatch.title = t.name;
      swatch.setAttribute('data-theme-id', t.id);
      swatch.addEventListener('click', function () { applyLoaderTheme(t.id); });
      swatchRow.appendChild(swatch);
    });
    themeSection.appendChild(swatchRow);
    dropdown.appendChild(themeSection);

    // Divider
    var div1 = document.createElement('div');
    div1.className = 'ava-menu-divider';
    dropdown.appendChild(div1);

    // ADO PAT item
    var adoItem = document.createElement('button');
    adoItem.className = 'ava-menu-item';
    adoItem.id = 'ava-menu-ado';
    adoItem.innerHTML = '<i class="fas fa-key"></i><span>ADO PAT</span><span class="ava-connected-dot" id="ava-ado-dot" style="display:none"></span>';
    adoItem.addEventListener('click', function (e) { e.stopPropagation(); toggleAdoPopover(); });
    dropdown.appendChild(adoItem);

    // Copilot item
    var copilotItem = document.createElement('button');
    copilotItem.className = 'ava-menu-item';
    copilotItem.id = 'ava-menu-copilot';
    copilotItem.innerHTML = '<i class="fab fa-github"></i><span>Copilot</span><span class="ava-connected-dot" id="ava-copilot-dot" style="display:none"></span>';
    copilotItem.addEventListener('click', function (e) { e.stopPropagation(); toggleCopilotPopover(); });
    dropdown.appendChild(copilotItem);

    // Divider before Workflow Builder
    var div2 = document.createElement('div');
    div2.className = 'ava-menu-divider';
    dropdown.appendChild(div2);

    // Workflow Builder item
    workflowMenuItem = document.createElement('button');
    workflowMenuItem.className = 'ava-menu-item';
    workflowMenuItem.innerHTML = '<i class="fas fa-project-diagram"></i><span>Workflow Builder</span>';
    workflowMenuItem.addEventListener('click', function () {
      if (currentView === 'chat') {
        showWorkflowBuilder();
      } else {
        showChat();
      }
      closeMenu();
    });
    dropdown.appendChild(workflowMenuItem);

    header.appendChild(dropdown);

    // Menu toggle logic
    menuBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      dropdown.classList.toggle('ava-menu-open');
    });
    document.addEventListener('click', function (e) {
      if (!dropdown.contains(e.target) && e.target !== menuBtn) {
        dropdown.classList.remove('ava-menu-open');
      }
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') dropdown.classList.remove('ava-menu-open');
    });

    panel.appendChild(header);

    var loading = document.createElement('div');
    loading.className = 'ava-loading';
    loading.id = 'ava-loading';
    loading.textContent = 'Loading AVA\u2026';
    panel.appendChild(loading);

    document.body.appendChild(panel);

    // Apply saved theme to header (must be after panel is in the DOM
    // so getElementById can find #ava-panel-header)
    applyHeaderColor(savedTheme);

    // Check auth status and show green dots if already connected
    if (apiUrl) checkAuthStatus();

    wireResize();
  }

  // ── Toggle ───────────────────────────────────────────────────────────────
  function toggle() {
    if (!panel) createPanel();
    isOpen = !isOpen;
    panel.classList.toggle('ava-panel-open', isOpen);
    toggleBtn.title = isOpen ? 'Minimize AVA' : 'Open AVA';

    // Hide/restore page scrollbar when panel is open
    document.documentElement.style.overflow = isOpen ? 'hidden' : '';
    document.body.style.overflow = isOpen ? 'hidden' : '';

    if (mode === 'fab') {
      toggleBtn.classList.toggle('ava-fab-active', isOpen);
    } else {
      toggleBtn.classList.toggle('ava-active', isOpen);
    }

    if (isOpen && !isLoaded) {
      loadAvaChat();
    }
  }

  // ── Lazy Load ────────────────────────────────────────────────────────────
  function loadAvaChat() {
    isLoaded = true;

    var authPromise = apiUrl
      ? fetch(apiUrl + '/api/auth/me?sessionId=' + encodeURIComponent(getSessionId()), { credentials: 'include' })
          .then(function (r) { return r.ok ? r.json() : {}; })
          .catch(function () { return {}; })
      : Promise.resolve({});

    var scriptPromise = new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.type = 'module';
      s.src = baseUrl + 'ava-chat.js?v=' + Date.now();
      s.onload = resolve;
      s.onerror = reject;
      document.head.appendChild(s);
    });

    Promise.all([authPromise, scriptPromise])
      .then(function (results) {
        var user = results[0];
        return customElements.whenDefined('ava-chat').then(function () {
          return user;
        });
      })
      .then(function (user) {
        var loading = document.getElementById('ava-loading');
        if (loading) loading.remove();

        var el = document.createElement('ava-chat');
        if (apiUrl) el.setAttribute('api-url', apiUrl);
        el.setAttribute('app-context', appContext);
        if (user && user.token) el.setAttribute('auth-token', user.token);
        panel.appendChild(el);

        // Inject Font Awesome CSS into the shadow root so icons render.
        // Polls until the shadow root exists (Angular creates it async).
        (function injectFa(attempts) {
          var sr = el.shadowRoot;
          if (!sr) {
            if (attempts < 50) setTimeout(function () { injectFa(attempts + 1); }, 100);
            return;
          }
          if (sr.querySelector('link[href*="font-awesome"], link[href*="fontawesome"]')) return;

          // Clone the host page's FA <link> (works even on internal CDNs)
          var hostFa = document.querySelector('link[href*="font-awesome"], link[href*="fontawesome"]');
          if (hostFa) {
            sr.appendChild(hostFa.cloneNode(true));
          } else {
            var link = document.createElement('link');
            link.rel = 'stylesheet';
            link.href = 'https://pro.fontawesome.com/releases/v5.10.0/css/all.css';
            link.crossOrigin = 'anonymous';
            sr.appendChild(link);
          }

          // Inject Material Icons font so <mat-icon> ligatures render
          if (!sr.querySelector('link[href*="Material+Icons"]')) {
            var miLink = document.createElement('link');
            miLink.rel = 'stylesheet';
            miLink.href = 'https://fonts.googleapis.com/icon?family=Material+Icons';
            sr.appendChild(miLink);
          }
        })(0);
      })
      .catch(function (err) {
        console.error('[AVA] Failed to load:', err);
        var loading = document.getElementById('ava-loading');
        if (loading) loading.textContent = 'Failed to load AVA. Please try again.';
        isLoaded = false;
      });
  }

  // ── View Switching ────────────────────────────────────────────────────────
  function showWorkflowBuilder() {
    var chatEl = panel ? panel.querySelector('ava-chat') : null;
    if (chatEl) chatEl.style.display = 'none';

    if (!workflowFrame) {
      workflowFrame = document.createElement('iframe');
      workflowFrame.id = 'ava-workflow-frame';
      workflowFrame.style.cssText = 'width:100%;flex:1;border:none;min-height:0;';
      workflowFrame.src = adminUrl + '/workflow-builder?embed=true';
      panel.appendChild(workflowFrame);
    }
    workflowFrame.classList.add('ava-view-active');

    currentView = 'workflows';
    if (panelTitle) panelTitle.textContent = 'AVA \u2014 Workflows';
    if (workflowMenuItem) {
      workflowMenuItem.innerHTML = '<i class="fas fa-comments"></i><span>Back to Chat</span>';
    }
  }

  function showChat() {
    if (workflowFrame) workflowFrame.classList.remove('ava-view-active');

    var chatEl = panel ? panel.querySelector('ava-chat') : null;
    if (chatEl) chatEl.style.display = 'flex';

    currentView = 'chat';
    if (panelTitle) panelTitle.textContent = 'AVA';
    if (workflowMenuItem) {
      workflowMenuItem.innerHTML = '<i class="fas fa-project-diagram"></i><span>Workflow Builder</span>';
    }
  }

  // ── Resize ───────────────────────────────────────────────────────────────
  function wireResize() {
    var startX, startWidth;

    handle.addEventListener('mousedown', function (e) {
      e.preventDefault();
      startX = e.clientX;
      startWidth = panel.offsetWidth;
      handle.classList.add('ava-dragging');
      // Prevent iframe from stealing mouse events during resize
      panel.classList.add('ava-resizing');
      document.body.style.userSelect = 'none';

      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });

    function onMove(e) {
      var delta = startX - e.clientX;
      var maxW = window.innerWidth - 50;
      var w = Math.min(maxW, Math.max(MIN_WIDTH, startWidth + delta));
      panel.style.width = w + 'px';
    }

    function onUp() {
      handle.classList.remove('ava-dragging');
      panel.classList.remove('ava-resizing');
      document.body.style.userSelect = '';
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      saveWidth(panel.offsetWidth);
    }
  }

  // ── LocalStorage ─────────────────────────────────────────────────────────
  function getSavedWidth() {
    try {
      var maxW = window.innerWidth - 50;
      var w = parseInt(localStorage.getItem(STORAGE_KEY), 10);
      return w >= MIN_WIDTH && w <= maxW ? w : DEFAULT_WIDTH;
    } catch (e) {
      return DEFAULT_WIDTH;
    }
  }

  function saveWidth(w) {
    try {
      localStorage.setItem(STORAGE_KEY, String(w));
    } catch (e) { /* localStorage may be disabled */ }
  }

  // ── Theme & Menu Helpers ─────────────────────────────────────────────────
  function setThemeCookie(themeId) {
    document.cookie = 'ava-theme=' + encodeURIComponent(themeId) + ';path=/;max-age=31536000;SameSite=Lax';
  }

  function getThemeCookie() {
    var match = document.cookie.match('(^|;)\\s*ava-theme=([^;]*)');
    return match ? decodeURIComponent(match[2]) : null;
  }

  function getThemeDef(id) {
    for (var i = 0; i < AVA_THEMES.length; i++) {
      if (AVA_THEMES[i].id === id) return AVA_THEMES[i];
    }
    return AVA_THEMES[0];
  }

  function applyHeaderColor(themeId) {
    var header = document.getElementById('ava-panel-header');
    if (header) header.style.background = getThemeDef(themeId).header;
  }

  function updateSwatchHighlight(themeId) {
    var swatches = document.querySelectorAll('.ava-menu-swatch');
    swatches.forEach(function (s) {
      s.classList.toggle('ava-swatch-active', s.getAttribute('data-theme-id') === themeId);
    });
  }

  var themeChannel = (typeof BroadcastChannel !== 'undefined')
    ? new BroadcastChannel('ava-theme')
    : null;

  function applyLoaderTheme(themeId) {
    try { localStorage.setItem(THEME_STORAGE_KEY, themeId); } catch (e) {}
    setThemeCookie(themeId);
    applyHeaderColor(themeId);
    updateSwatchHighlight(themeId);
    closeMenu();
    // Notify ava-web (same page + same-origin tabs)
    if (themeChannel) themeChannel.postMessage(themeId);
  }

  function closeMenu() {
    var dropdown = document.getElementById('ava-menu-dropdown');
    if (dropdown) dropdown.classList.remove('ava-menu-open');
  }

  // ── Auth State & API ──────────────────────────────────────────────────────
  var SESSION_ID_KEY = 'ava-session-id';
  var adoConnected = false;
  var copilotConnected = false;
  var copilotPending = false;
  var copilotUserCode = null;
  var copilotVerificationUri = null;
  var copilotDeviceCode = null;
  var copilotPollTimer = null;
  var activePopover = null; // 'ado' | 'copilot' | null

  function getSessionId() {
    var id = localStorage.getItem(SESSION_ID_KEY);
    if (!id) {
      id = crypto.randomUUID();
      localStorage.setItem(SESSION_ID_KEY, id);
    }
    return id;
  }

  function checkAuthStatus() {
    var sid = getSessionId();
    fetch(apiUrl + '/api/auth/ado/status?sessionId=' + encodeURIComponent(sid), { credentials: 'include' })
      .then(function (r) { return r.ok ? r.json() : { connected: false }; })
      .then(function (res) {
        adoConnected = !!res.connected;
        updateAuthDots();
      })
      .catch(function () {});

    fetch(apiUrl + '/api/auth/github/status?sessionId=' + encodeURIComponent(sid), { credentials: 'include' })
      .then(function (r) { return r.ok ? r.json() : { connected: false }; })
      .then(function (res) {
        copilotConnected = !!res.connected;
        updateAuthDots();
      })
      .catch(function () {});
  }

  function connectAdoPat(pat) {
    var sid = getSessionId();
    fetch(apiUrl + '/api/auth/ado/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ sessionId: sid, pat: pat }),
    })
      .then(function (r) {
        if (r.ok) {
          adoConnected = true;
          updateAuthDots();
          closeAuthPopover();
        } else {
          console.error('[AVA] ADO connect failed', r.status);
        }
      })
      .catch(function (err) { console.error('[AVA] ADO connect error', err); });
  }

  function disconnectAdo() {
    var sid = getSessionId();
    fetch(apiUrl + '/api/auth/ado/session?sessionId=' + encodeURIComponent(sid), {
      method: 'DELETE',
      credentials: 'include',
    })
      .then(function () {
        adoConnected = false;
        updateAuthDots();
        renderAdoPopoverContent();
      })
      .catch(function (err) { console.error('[AVA] ADO disconnect error', err); });
  }

  function startCopilotFlow() {
    fetch(apiUrl + '/api/auth/github/start', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: '{}',
    })
      .then(function (r) { return r.json(); })
      .then(function (res) {
        copilotPending = true;
        copilotUserCode = res.userCode;
        copilotVerificationUri = res.verificationUri;
        copilotDeviceCode = res.deviceCode;
        renderCopilotPopoverContent();
        startCopilotPolling(res.interval || 5);
      })
      .catch(function (err) { console.error('[AVA] Copilot start error', err); });
  }

  function startCopilotPolling(intervalSecs) {
    stopCopilotPolling();
    copilotPollTimer = setInterval(function () {
      if (!copilotDeviceCode) { stopCopilotPolling(); return; }
      var sid = getSessionId();
      fetch(apiUrl + '/api/auth/github/poll', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ deviceCode: copilotDeviceCode, sessionId: sid }),
      })
        .then(function (r) { return r.json(); })
        .then(function (res) {
          if (res.status === 'authorized') {
            stopCopilotPolling();
            copilotPending = false;
            copilotConnected = true;
            copilotUserCode = null;
            updateAuthDots();
            renderCopilotPopoverContent();
          } else if (res.status === 'expired' || res.status === 'denied') {
            stopCopilotPolling();
            copilotPending = false;
            copilotUserCode = null;
            renderCopilotPopoverContent();
          } else if (res.status === 'slow_down' && res.newInterval) {
            startCopilotPolling(res.newInterval);
          }
        })
        .catch(function () { /* transient — keep polling */ });
    }, intervalSecs * 1000);
  }

  function stopCopilotPolling() {
    if (copilotPollTimer) { clearInterval(copilotPollTimer); copilotPollTimer = null; }
  }

  function disconnectCopilot() {
    stopCopilotPolling();
    var sid = getSessionId();
    fetch(apiUrl + '/api/auth/github/session?sessionId=' + encodeURIComponent(sid), {
      method: 'DELETE',
      credentials: 'include',
    })
      .then(function () {
        copilotConnected = false;
        copilotPending = false;
        copilotUserCode = null;
        copilotDeviceCode = null;
        updateAuthDots();
        renderCopilotPopoverContent();
      })
      .catch(function (err) { console.error('[AVA] Copilot disconnect error', err); });
  }

  function updateAuthDots() {
    var adoDot = document.getElementById('ava-ado-dot');
    var copilotDot = document.getElementById('ava-copilot-dot');
    if (adoDot) adoDot.style.display = adoConnected ? 'block' : 'none';
    if (copilotDot) copilotDot.style.display = copilotConnected ? 'block' : 'none';
  }

  // ── Auth Popover Rendering ────────────────────────────────────────────────
  function closeAuthPopover() {
    var existing = document.getElementById('ava-auth-popover');
    if (existing) existing.remove();
    activePopover = null;
  }

  function toggleAdoPopover() {
    if (activePopover === 'ado') { closeAuthPopover(); return; }
    closeAuthPopover();
    closeMenu();
    activePopover = 'ado';

    var pop = document.createElement('div');
    pop.id = 'ava-auth-popover';
    pop.className = 'ava-auth-popover';
    renderAdoPopoverInner(pop);

    var header = document.getElementById('ava-panel-header');
    if (header) { header.style.position = 'relative'; header.appendChild(pop); }
  }

  function renderAdoPopoverContent() {
    var pop = document.getElementById('ava-auth-popover');
    if (!pop || activePopover !== 'ado') return;
    renderAdoPopoverInner(pop);
  }

  function renderAdoPopoverInner(pop) {
    pop.innerHTML = '';

    var title = document.createElement('div');
    title.className = 'ava-auth-popover-title';
    title.innerHTML = '<i class="fas fa-key"></i><span>ADO PAT</span>';
    var closeBtn = document.createElement('button');
    closeBtn.className = 'ava-auth-popover-close';
    closeBtn.innerHTML = '<i class="fas fa-times"></i>';
    closeBtn.addEventListener('click', closeAuthPopover);
    title.appendChild(closeBtn);
    pop.appendChild(title);

    if (adoConnected) {
      var msg = document.createElement('div');
      msg.className = 'ava-auth-connected-msg';
      msg.innerHTML = '<i class="fas fa-check-circle"></i><span>Connected</span>';
      pop.appendChild(msg);

      var row = document.createElement('div');
      row.className = 'ava-auth-row';
      row.style.marginTop = '10px';
      var disconnBtn = document.createElement('button');
      disconnBtn.className = 'ava-auth-btn ava-btn-danger';
      disconnBtn.textContent = 'Disconnect';
      disconnBtn.addEventListener('click', disconnectAdo);
      row.appendChild(disconnBtn);
      pop.appendChild(row);
    } else {
      var row = document.createElement('div');
      row.className = 'ava-auth-row';
      var input = document.createElement('input');
      input.type = 'password';
      input.placeholder = 'Paste your PAT';
      input.id = 'ava-ado-pat-input';
      var btn = document.createElement('button');
      btn.className = 'ava-auth-btn';
      btn.textContent = 'Connect';
      btn.addEventListener('click', function () {
        var val = input.value.trim();
        if (val) connectAdoPat(val);
      });
      input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') {
          var val = input.value.trim();
          if (val) connectAdoPat(val);
        }
      });
      row.appendChild(input);
      row.appendChild(btn);
      pop.appendChild(row);
      setTimeout(function () { input.focus(); }, 50);
    }
  }

  function toggleCopilotPopover() {
    if (activePopover === 'copilot') { closeAuthPopover(); return; }
    closeAuthPopover();
    closeMenu();
    activePopover = 'copilot';

    var pop = document.createElement('div');
    pop.id = 'ava-auth-popover';
    pop.className = 'ava-auth-popover';
    renderCopilotPopoverInner(pop);

    var header = document.getElementById('ava-panel-header');
    if (header) { header.style.position = 'relative'; header.appendChild(pop); }
  }

  function renderCopilotPopoverContent() {
    var pop = document.getElementById('ava-auth-popover');
    if (!pop || activePopover !== 'copilot') return;
    renderCopilotPopoverInner(pop);
  }

  function renderCopilotPopoverInner(pop) {
    pop.innerHTML = '';

    var title = document.createElement('div');
    title.className = 'ava-auth-popover-title';
    title.innerHTML = '<i class="fab fa-github"></i><span>GitHub Copilot</span>';
    var closeBtn = document.createElement('button');
    closeBtn.className = 'ava-auth-popover-close';
    closeBtn.innerHTML = '<i class="fas fa-times"></i>';
    closeBtn.addEventListener('click', function () { stopCopilotPolling(); closeAuthPopover(); });
    title.appendChild(closeBtn);
    pop.appendChild(title);

    if (copilotConnected) {
      var msg = document.createElement('div');
      msg.className = 'ava-auth-connected-msg';
      msg.innerHTML = '<i class="fas fa-check-circle"></i><span>Connected</span>';
      pop.appendChild(msg);

      var row = document.createElement('div');
      row.className = 'ava-auth-row';
      row.style.marginTop = '10px';
      var disconnBtn = document.createElement('button');
      disconnBtn.className = 'ava-auth-btn ava-btn-danger';
      disconnBtn.textContent = 'Disconnect';
      disconnBtn.addEventListener('click', disconnectCopilot);
      row.appendChild(disconnBtn);
      pop.appendChild(row);
    } else if (copilotPending && copilotUserCode) {
      var codeDiv = document.createElement('div');
      codeDiv.className = 'ava-auth-device-code';
      codeDiv.innerHTML =
        '<span class="ava-hint">Enter this code on GitHub:</span>' +
        '<span class="ava-code">' + copilotUserCode + '</span>';
      var openBtn = document.createElement('button');
      openBtn.className = 'ava-auth-btn';
      openBtn.innerHTML = '<i class="fas fa-external-link-alt"></i> Open GitHub';
      openBtn.addEventListener('click', function () {
        if (copilotVerificationUri) window.open(copilotVerificationUri, '_blank', 'noopener');
      });
      codeDiv.appendChild(openBtn);
      pop.appendChild(codeDiv);
    } else {
      var row = document.createElement('div');
      row.className = 'ava-auth-row';
      var btn = document.createElement('button');
      btn.className = 'ava-auth-btn';
      btn.innerHTML = '<i class="fab fa-github"></i> Start Device Flow';
      btn.addEventListener('click', startCopilotFlow);
      row.appendChild(btn);
      pop.appendChild(row);
    }
  }

  // Close popover on outside click
  document.addEventListener('click', function (e) {
    var pop = document.getElementById('ava-auth-popover');
    if (!pop || !activePopover) return;
    // Check if click is inside the popover or on the menu items that toggle it
    if (pop.contains(e.target)) return;
    var adoItem = document.getElementById('ava-menu-ado');
    var copilotItem = document.getElementById('ava-menu-copilot');
    if ((adoItem && adoItem.contains(e.target)) || (copilotItem && copilotItem.contains(e.target))) return;
    closeAuthPopover();
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && activePopover) closeAuthPopover();
  });

  // Listen for theme changes from ava-web (same page + same-origin tabs)
  if (themeChannel) {
    themeChannel.onmessage = function (e) {
      var themeId = e.data;
      if (themeId) {
        try { localStorage.setItem(THEME_STORAGE_KEY, themeId); } catch (ex) {}
        setThemeCookie(themeId);
        applyHeaderColor(themeId);
        updateSwatchHighlight(themeId);
      }
    };
  }

  // Cross-origin sync: check cookie when tab gains focus
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible') {
      var cookieTheme = getThemeCookie();
      if (cookieTheme && cookieTheme !== localStorage.getItem(THEME_STORAGE_KEY)) {
        try { localStorage.setItem(THEME_STORAGE_KEY, cookieTheme); } catch (ex) {}
        applyHeaderColor(cookieTheme);
        updateSwatchHighlight(cookieTheme);
      }
    }
  });

  // ── Init ─────────────────────────────────────────────────────────────────
  function init() {
    injectFonts();
    injectStyles();

    if (targetSelector) {
      // The target element may be rendered by a framework (Angular, React, etc.)
      // after DOMContentLoaded, so poll for it with a MutationObserver.
      var container = document.querySelector(targetSelector);
      if (container) {
        createHeaderButton(container);
        return;
      }

      var observer = new MutationObserver(function () {
        var el = document.querySelector(targetSelector);
        if (el) {
          observer.disconnect();
          createHeaderButton(el);
        }
      });
      observer.observe(document.body, { childList: true, subtree: true });

      // Safety timeout — fall back to FAB if target never appears
      setTimeout(function () {
        observer.disconnect();
        if (!toggleBtn) {
          console.warn('[AVA] data-target "' + targetSelector + '" not found after 10s, falling back to FAB.');
          createFab();
        }
      }, 10000);
      return;
    }
    createFab();
  }

  // Run init after DOM is ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
