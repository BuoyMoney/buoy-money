// @ts-check
import fs from 'node:fs';
import path from 'node:path';
import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';
import starlight from '@astrojs/starlight';
import finalizeHtml from './integrations/finalize-html.mjs';

// Blog frontmatter dates, read straight off disk so the sitemap can carry a real
// `lastmod`. Without one, crawlers get no freshness signal at all — and AI answer
// engines weight recency heavily when choosing what to cite.
//
// Parsed with a regex rather than the content collection because this runs while the
// Astro config is being evaluated, before collections exist. Only blog URLs get a
// lastmod: stamping every static page with the BUILD date would be a lie, and a
// sitemap where all dates move together on every deploy is discounted outright.
const BLOG_DIR = './src/content/blog';
const blogLastmod = new Map();
for (const file of fs.readdirSync(BLOG_DIR)) {
  if (!file.endsWith('.md')) continue;
  const src = fs.readFileSync(path.join(BLOG_DIR, file), 'utf8');
  const pub = src.match(/^pubDate:\s*['"]?(\d{4}-\d{2}-\d{2})/m)?.[1];
  const updated = src.match(/^updatedDate:\s*['"]?(\d{4}-\d{2}-\d{2})/m)?.[1];
  const date = updated ?? pub;
  if (date) blogLastmod.set(`/blog/${file.replace(/\.md$/, '')}`, date);
}

// The marketing site is served at the domain root. The Flutter app lives under
// /app/ (a separate build), so the sitemap must never advertise /app/* URLs.
// `build.format: 'file'` emits /pricing.html etc. — simpler nginx static serving.
export default defineConfig({
  site: 'https://buoy-ai.com',
  trailingSlash: 'never',
  build: { format: 'file' },
  integrations: [
    // How-to docs under /docs (Starlight: sidebar, search, page furniture). Content is
    // src/content/docs/docs/**; the extra docs/ level is what puts the routes under /docs.
    starlight({
      title: 'Buoy Docs',
      logo: { src: './src/assets/logo-mark.png', alt: 'Buoy' },
      favicon: '/favicon.ico',
      // The site has its own 404 page; Starlight would otherwise claim /404.
      disable404Route: true,
      customCss: ['./src/styles/docs.css'],
      components: {
        SocialIcons: './src/components/docs/HeaderLinks.astro',
        Footer: './src/components/docs/Footer.astro',
      },
      head: [
        { tag: 'meta', attrs: { property: 'og:image', content: 'https://buoy-ai.com/og-image.png' } },
        { tag: 'meta', attrs: { name: 'twitter:card', content: 'summary_large_image' } },
      ],
      // One folder per section under src/content/docs/docs/; order inside a section comes
      // from each page's sidebar.order.
      sidebar: [
        { label: 'Getting started', items: [{ autogenerate: { directory: 'docs/start' } }] },
        { label: 'Budgets', items: [{ autogenerate: { directory: 'docs/budgets' } }] },
        { label: 'Banks and transactions', items: [{ autogenerate: { directory: 'docs/banks' } }] },
        { label: 'Family sharing', items: [{ autogenerate: { directory: 'docs/family' } }] },
        { label: 'AI', items: [{ autogenerate: { directory: 'docs/ai' } }] },
        { label: 'Streaks and rewards', items: [{ autogenerate: { directory: 'docs/rewards' } }] },
        { label: 'Account and billing', items: [{ autogenerate: { directory: 'docs/account' } }] },
        // Self-hosting and BuoyAI Cloud: every page is `draft: true` until launch, so production builds leave the
        // pages out, and this group with them. `astro dev` shows both for review.
        ...(process.env.NODE_ENV === 'production'
          ? []
          : [{ label: 'Run it yourself', items: [{ autogenerate: { directory: 'docs/selfhost' } }] }]),
      ],
    }),
    sitemap({
      // Never advertise the Flutter app, and never advertise a page that carries a
      // noindex meta tag: listing a URL in the sitemap while telling crawlers to ignore
      // it is a contradictory signal, and /unsubscribe is a functional endpoint reached
      // from an email, not content anyone should discover through search.
      filter: (page) =>
        !page.includes('/app/') && !page.includes('/unsubscribe') &&
        !page.includes('/founding/thanks') &&
        !page.includes('/oauth/'),
      serialize(item) {
        const pathname = new URL(item.url).pathname.replace(/\/$/, '');
        const date = blogLastmod.get(pathname);
        if (date) item.lastmod = new Date(`${date}T00:00:00Z`).toISOString();
        // Astro 7 writes the root as https://buoy-ai.com/; keep it matching the canonical tag.
        if (pathname === '') item.url = item.url.replace(/\/$/, '');
        return item;
      },
    }),
    // Last, so it sees the final HTML: no inline scripts (production CSP) and clean URLs.
    finalizeHtml(),
  ],
});
