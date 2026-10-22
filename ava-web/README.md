# AvaWeb

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 20.1.3.

# AVA Loader — Integration Guide

Drop a single `<script>` tag into any application to embed the AVA assistant sidebar.

---

## Integration Modes

### Option 1 — Floating FAB button (default)

A circular button is fixed to the bottom-right corner of the page. No additional configuration required.

```html
<script src="http://localhost:9090/ava-loader.js"
        data-app="YourAppName"
        data-api-url="http://localhost:5010"></script>
```

### Option 2 — Injected into an existing header element

The AVA toggle button is inserted as the **first child** of the element matched by `data-target`. Falls back to the floating FAB if the selector does not match anything.

```html
<script src="http://localhost:9090/ava-loader.js"
        data-app="YourAppName"
        data-api-url="http://localhost:5010"
        data-target=".site-header .action-item-container"></script>
```

---

## Available Attributes

| Attribute | Required | Description | Default |
|---|:---:|---|---|
| `data-app` | | App context name sent to AVA — used to scope conversations | `"default"` |
| `data-api-url` | | AVA API base URL | Derived from loader origin — replaces `ava.` → `ava-api.` |
| `data-admin-url` | | Orchestration UI URL (Workflow Builder) | Derived from loader origin — replaces `ava.` → `ava-admin.` on prod; `localhost:6201` locally |
| `data-target` | | CSS selector of the element to inject the toggle button into as its first child | Floating FAB |

---

## Local vs Production URLs

| Service | Local | Production |
|---|---|---|
| Loader script | `http://localhost:9090/ava-loader.js` | TBD |
| API (`data-api-url`) | `http://localhost:5010` | TBD |
| Admin UI (`data-admin-url`) | `http://localhost:6201` | TBD |

> **Note:** When using the production loader URL (`https://ava.yourcompany.com/ava-loader.js`), `data-api-url` and `data-admin-url` are automatically derived from the loader origin and can be omitted.

---

## Full Example (header injection)

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>My App</title>
</head>
<body>
  <header class="site-header">
    <span>My Application</span>
    <div class="action-item-container">
      <button>Menu</button>
      <!-- AVA button will be injected here as the first child -->
    </div>
  </header>

  <main>Page content</main>

  <script src="http://localhost:9090/ava-loader.js"
          data-app="MyApp"
          data-api-url="http://localhost:5010"
          data-target=".site-header .action-item-container"></script>
</body>
</html>
```
## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:6200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Karma](https://karma-runner.github.io) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
