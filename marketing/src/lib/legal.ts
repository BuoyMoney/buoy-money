import { readFileSync } from 'node:fs';
import path from 'node:path';
import { marked } from 'marked';

// Renders a legal Markdown document from the repo-root `legal/` directory, the
// SAME source of truth the Flutter app bundles (legal/*.md, synced into
// flutter/assets/legal/). Edit the markdown once and both surfaces update.
//
// Resolved from the build's working directory, which is `marketing/` in CI, in
// Dockerfile.flutter and locally, with `legal/` as its sibling. It used to resolve from
// import.meta.url, but Astro 7 prerenders from bundled chunks under dist/.prerender/,
// so a path relative to this source file no longer points anywhere real.
export function renderLegal(filename: string): string {
  const file = path.resolve(process.cwd(), '..', 'legal', filename);
  const markdown = readFileSync(file, 'utf-8');
  return marked.parse(markdown, { async: false }) as string;
}
