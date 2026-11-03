import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

// Blog posts authored as Markdown under src/content/blog/.
const blog = defineCollection({
  loader: glob({ pattern: '**/*.md', base: './src/content/blog' }),
  schema: z.object({
    title: z.string(),
    description: z.string(),
    pubDate: z.coerce.date(),
    updatedDate: z.coerce.date().optional(),
    // Named human by default. Anonymous team bylines are the weakest E-E-A-T signal in
    // YMYL finance; the JSON-LD author resolves to the founder Person node either way.
    author: z.string().default('Jonathan Barnell'),
    draft: z.boolean().default(false),
  }),
});

// How-to docs rendered by Starlight at /docs (see astro.config.mjs).
const docs = defineCollection({ loader: docsLoader(), schema: docsSchema() });

export const collections = { blog, docs };
