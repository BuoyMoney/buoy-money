import { marked } from 'marked';

/** Render a Markdown string to HTML (build-time, synchronous). */
export function renderMarkdown(md: string): string {
  return marked.parse(md, { async: false }) as string;
}
