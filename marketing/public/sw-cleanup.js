// Migration cleanup: the old Flutter app was served at the domain root and
// registered a service worker scoped to "/". The root is now the Astro site and
// Flutter lives under /app/ (its worker scopes to /app/). Unregister any stale
// root-scoped worker so returning visitors aren't served the old cached shell.
// Served as a static file (not bundled/inlined) so it satisfies CSP script-src 'self'.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.getRegistrations().then((regs) => {
    for (const r of regs) {
      if (r.scope && new URL(r.scope).pathname === '/') r.unregister();
    }
  });
}
