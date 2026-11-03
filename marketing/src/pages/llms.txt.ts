import type { APIRoute } from 'astro';
import { getCollection } from 'astro:content';
import { SITE } from '../consts';
import { PERSONAS } from '../data/seo';

// llms.txt — a concise, link-rich map of the site for AI crawlers / answer
// engines (emerging community standard). Generated at build so it stays in sync.
export const GET: APIRoute = async () => {
  const u = (p: string) => `${SITE.url}${p}`;
  const posts = (await getCollection('blog', ({ data }) => !data.draft)).sort(
    (a, b) => b.data.pubDate.getTime() - a.data.pubDate.getTime() || a.id.localeCompare(b.id)
  );

  // How-to docs (Starlight, /docs): what the app does and how to use it, written for
  // current users. The inbox reply drafter and answer engines read product facts here.
  const docs = (await getCollection('docs'))
    .filter((d) => d.id !== 'docs')
    .sort((a, b) => a.id.localeCompare(b.id));

  const body = `# ${SITE.name}

> ${SITE.name} is a personal-finance app built around "count-up budgeting": each spending category earns a small daily allowance that grows when you underspend, so saving feels like winning. It pairs this with streaks, achievement badges, seven levels (Beginner to Legend), subscription tracking, an AI budgeting advisor, and read-only bank linking through Plaid. Plans: Standard $10/mo or $100/yr; Premium $15/mo or $150/yr (free trial, no card required).

## Key pages
- [Home](${u('/')}): count-up budgeting, how it works, pricing
- [What is count-up budgeting?](${u('/count-up-budgeting')}): the definitive guide to the count-up method vs zero-based and envelope budgeting
- [Pricing](${u('/pricing')}): Standard and Premium plans, annual and monthly
- [Best Mint alternatives (2026)](${u('/mint-alternatives')}): Buoy compared with YNAB, Monarch, Copilot, and Rocket Money
- [Daily allowance calculator](${u('/calculator')}): a free tool that turns a monthly budget into a daily allowance
- [Security & privacy](${u('/security')}): read-only Plaid access, encryption, data handling
- [About](${u('/about')}): mission and the count-up philosophy

## Guides for specific situations
${PERSONAS.map((p) => `- [${p.title}](${u('/' + p.slug)}): ${p.metaDescription}`).join('\n')}

## Help docs (how to use Buoy)
- [Buoy docs home](${u('/docs')}): every how-to guide, grouped by feature
${docs.map((d) => `- [${d.data.title}](${u('/' + d.id)}): ${d.data.description ?? ''}`).join('\n')}

## Blog
${posts.map((p) => `- [${p.data.title}](${u('/blog/' + p.id)}): ${p.data.description}`).join('\n')}

## Notes
- The product app lives at ${u('/app')} and is a CanvasKit (Flutter) application — it is intentionally not part of this crawlable content; everything above is the public, factual marketing content.
`;

  return new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
};
