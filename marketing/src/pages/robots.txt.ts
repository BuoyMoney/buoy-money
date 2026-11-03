import type { APIRoute } from 'astro';
import { SITE } from '../consts';

// Environment-aware robots.txt, emitted at build time. Production allows the
// public marketing site. Any non-production build (staging) disallows everything as
// a belt-and-suspenders alongside nginx's X-Robots-Tag and IP allowlist.
//
// Production deliberately does NOT disallow /app/, even though we don't want the
// Flutter app in the index. Disallow blocks crawling, not indexing — Google indexed
// /app/ URLs it found through links and, unable to fetch them, never saw that we
// wanted them out ("Indexed, though blocked by robots.txt"). Crawling is the only way
// it can read the X-Robots-Tag: noindex that nginx serves on /app/, so the block has
// to come off for the noindex to work. Keep this in sync with nginx-flutter.conf.
const isProd = (process.env.ENVIRONMENT ?? 'development') === 'production';

const body = isProd
  ? `User-agent: *
Allow: /

Sitemap: ${SITE.url}/sitemap-index.xml
`
  : `User-agent: *
Disallow: /
`;

export const GET: APIRoute = () =>
  new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
