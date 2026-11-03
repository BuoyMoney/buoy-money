// Last pass over the built HTML, for two production constraints the frameworks don't know
// about.
//
// 1. No inline scripts. Production serves the site under a CSP whose script-src has no
//    'unsafe-inline' (nginx-csp-production.conf, baked into the app image and only shipped
//    on a v* tag). Our own pages keep all behaviour in external files, but Starlight's
//    components (theme picker, mobile menu, sidebar state, search shortcut) render small
//    inline scripts the browser would refuse, leaving /docs half-working in production
//    only. Each executable inline script moves into a content-hashed /_astro/ file at the
//    same position: a classic script stays classic (synchronous, in order), a module stays
//    a module. JSON-LD and other data types are left alone.
//
// 2. Clean URLs on docs pages. With build.format 'file', Starlight writes links and the
//    canonical tag as /docs/x.html and the logo link as /index.html. nginx 301s every
//    *.html to the clean path, so the links work, but a canonical tag that points at a
//    redirect is a bad search signal. Rewrites same-site hrefs and the canonical/og:url on
//    pages under /docs to the clean form the sitemap already uses.
import { createHash } from 'node:crypto';
import { readdir, readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;
const EXECUTABLE = new Set(['', 'text/javascript', 'application/javascript', 'module']);
const SITE = 'https://buoy-ai.com';

async function* htmlFiles(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* htmlFiles(full);
    else if (entry.name.endsWith('.html')) yield full;
  }
}

function cleanUrl(url) {
  const m = url.match(/^((?:https:\/\/buoy-ai\.com)?\/[^"#?]*?)(index)?\.html([#?][^"]*)?$/);
  if (!m) return url;
  let base = m[1];
  if (m[2] === 'index') base = base.replace(/\/$/, '') || '/';
  if (base === SITE) base = SITE;
  return base + (m[3] ?? '');
}

export default function finalizeHtml() {
  return {
    name: 'buoy:finalize-html',
    hooks: {
      'astro:build:done': async ({ dir, logger }) => {
        const out = fileURLToPath(dir);
        const bodies = new Map(); // file name -> body; identical scripts share one file
        let moved = 0;
        let cleaned = 0;
        for await (const file of htmlFiles(out)) {
          const rel = path.relative(out, file).split(path.sep).join('/');
          let html = await readFile(file, 'utf8');
          const before = html;

          html = html.replace(SCRIPT, (tag, attrs, body) => {
            if (/\bsrc\s*=/.test(attrs) || !body.trim()) return tag;
            const type = (attrs.match(/\btype\s*=\s*["']?([^"'\s>]+)/i)?.[1] ?? '').toLowerCase();
            if (!EXECUTABLE.has(type)) return tag;
            const name = `inline.${createHash('sha256').update(body).digest('hex').slice(0, 12)}.js`;
            bodies.set(name, body);
            moved++;
            return `<script${attrs} src="/_astro/${name}"></script>`;
          });

          if (rel === 'docs.html' || rel.startsWith('docs/')) {
            html = html.replace(/(\s(?:href|content)=")([^"]+\.html(?:[#?][^"]*)?)(")/g, (all, pre, url, post) => {
              if (url.startsWith('/_astro/')) return all;
              const next = cleanUrl(url);
              if (next !== url) cleaned++;
              return pre + next + post;
            });
          }

          if (html !== before) await writeFile(file, html);
        }
        if (bodies.size) {
          const assets = path.join(out, '_astro');
          await mkdir(assets, { recursive: true });
          for (const [name, body] of bodies) await writeFile(path.join(assets, name), body);
        }
        logger.info(`moved ${moved} inline script(s) into ${bodies.size} file(s); cleaned ${cleaned} docs URL(s)`);
      },
    },
  };
}
