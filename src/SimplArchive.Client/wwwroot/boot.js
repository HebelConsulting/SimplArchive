// Starts Blazor with the language chosen before the runtime boots (ADR "Web UI localization"), so the resource
// accessor resolves to that language on the very first render.
//
// This lives in a file rather than inline in index.html so the content-security policy can refuse inline
// scripts outright (#844) — `script-src 'self' 'wasm-unsafe-eval'` with no `'unsafe-inline'`.
Blazor.start({ applicationCulture: window.simplArchiveLang.preferred() });
