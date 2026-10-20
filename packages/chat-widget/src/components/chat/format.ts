/** Inline Markdown: plain text, `code`, **strong**, *em* and links (http(s) only). */
export type Inline =
  | { kind: "text"; text: string }
  | { kind: "code"; text: string }
  | { kind: "strong"; children: Inline[] }
  | { kind: "em"; children: Inline[] }
  | { kind: "link"; href: string; children: Inline[] };

export type ListItem = { content: Inline[]; sublists?: ListBlock[] };
export type ListBlock = { kind: "ul"; items: ListItem[] } | { kind: "ol"; start: number; items: ListItem[] };
export type Block =
  | { kind: "p"; content: Inline[] }
  | { kind: "heading"; level: number; content: Inline[] }
  | { kind: "code"; text: string }
  | { kind: "quote"; content: Inline[] }
  | { kind: "hr" }
  | ListBlock;

const BULLET = /^(\s*)[-*+•]\s+(.*)$/;
const ORDERED = /^(\s*)(\d{1,9})[.)]\s+(.*)$/;
// A closing run of # is stripped only after whitespace, so "C#" keeps its #.
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$/;
const FENCE = /^\s{0,3}(```|~~~)/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const QUOTE = /^\s{0,3}>\s?(.*)$/;

/** A list while it is parsed: raw lines per item, so inline spans may cross line breaks. */
type DraftList = { kind: "ul" | "ol"; start: number; indent: number; items: { lines: string[]; subs: DraftList[] }[] };

/**
 * Parses reply text into a small, safe Markdown subset: paragraphs, headings, lists (one nesting level), fenced code,
 * quotes and rules. The result is plain data rendered as text nodes: nothing here is ever interpreted as HTML, and
 * raw HTML in the text stays visible text. Incomplete syntax (mid-stream) falls back to literal text.
 */
export function parseBlocks(text: string): Block[] {
  const blocks: Block[] = [];
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  let para: string[] = [];
  let quote: string[] = [];
  let list: DraftList | null = null;

  const flushPara = () => {
    if (para.length) blocks.push({ kind: "p", content: parseInline(para.join("\n")) });
    para = [];
  };
  const flushQuote = () => {
    if (quote.length) blocks.push({ kind: "quote", content: parseInline(quote.join("\n")) });
    quote = [];
  };
  const flushList = () => {
    if (list) blocks.push(finishList(list));
    list = null;
  };
  const flushAll = () => {
    flushPara();
    flushQuote();
    flushList();
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    const fence = FENCE.exec(line);
    if (fence) {
      flushAll();
      const code: string[] = [];
      // An unclosed fence (still streaming) runs to the end of the text.
      while (++i < lines.length && !lines[i]!.trimStart().startsWith(fence[1]!)) code.push(lines[i]!);
      blocks.push({ kind: "code", text: code.join("\n") });
      continue;
    }
    if (!line.trim()) {
      // A loose list goes on across blank lines while the next text is another item or indented detail.
      const next = lines.slice(i + 1).find((l) => l.trim());
      if (list && next !== undefined && (listItem(next) || /^\s/.test(next))) continue;
      flushAll();
      continue;
    }
    if (RULE.test(line)) {
      flushAll();
      blocks.push({ kind: "hr" });
      continue;
    }
    const item = listItem(line);
    if (item) {
      flushPara();
      flushQuote();
      const entry = { lines: [item.text], subs: [] as DraftList[] };
      const current = list as DraftList | null;
      if (current && item.indent > current.indent) {
        // Deeper than the list's own markers: nested under the previous item (deeper levels flatten into it).
        const parent = current.items[current.items.length - 1]!;
        const sub = parent.subs[parent.subs.length - 1];
        if (sub?.kind === item.kind) sub.items.push(entry);
        else parent.subs.push({ kind: item.kind, start: item.start, indent: item.indent, items: [entry] });
      } else if (current?.kind === item.kind) {
        current.items.push(entry);
      } else {
        flushList();
        list = { kind: item.kind, start: item.start, indent: item.indent, items: [entry] };
      }
      continue;
    }
    const current = list as DraftList | null;
    if (current && /^\s/.test(line)) {
      // Indented text continues an item: the nested one when it is indented past the nested markers.
      const parent = current.items[current.items.length - 1]!;
      const sub = parent.subs[parent.subs.length - 1];
      const indent = line.length - line.trimStart().length;
      (sub && indent > sub.indent ? sub.items[sub.items.length - 1]! : parent).lines.push(line.trim());
      continue;
    }
    const heading = HEADING.exec(line);
    if (heading) {
      flushAll();
      blocks.push({ kind: "heading", level: heading[1]!.length, content: parseInline(heading[2]!) });
      continue;
    }
    const q = QUOTE.exec(line);
    if (q) {
      flushPara();
      flushList();
      quote.push(q[1]!.trim());
      continue;
    }
    flushQuote();
    flushList();
    para.push(line.trim());
  }
  flushAll();
  return blocks;
}

function listItem(line: string): { indent: number; kind: "ul" | "ol"; start: number; text: string } | null {
  const b = BULLET.exec(line);
  if (b) return { indent: b[1]!.length, kind: "ul", start: 1, text: b[2]!.trim() };
  const o = ORDERED.exec(line);
  if (o) return { indent: o[1]!.length, kind: "ol", start: Number(o[2]), text: o[3]!.trim() };
  return null;
}

function finishList(d: DraftList): ListBlock {
  const items = d.items.map((it) => ({
    content: parseInline(it.lines.join("\n")),
    sublists: it.subs.length ? it.subs.map(finishList) : undefined,
  }));
  return d.kind === "ul" ? { kind: "ul", items } : { kind: "ol", start: d.start, items };
}

// The target may hold one level of balanced parentheses, as in https://en.wikipedia.org/wiki/Foo_(bar).
const LINK = /^\[([^\]\n]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)/;
const WORD = /\w/;

/**
 * Inline spans. An opening marker without its closing one stays literal text, so partial text streams cleanly.
 * Emphasis markers (* and _, single, double or triple) never open inside a word, so 2*3*4, snake_case and __init__
 * stay as written.
 */
export function parseInline(text: string): Inline[] {
  const out: Inline[] = [];
  let plain = "";
  const push = (...nodes: Inline[]) => {
    if (plain) out.push({ kind: "text", text: plain });
    plain = "";
    out.push(...nodes);
  };
  let i = 0;
  while (i < text.length) {
    const c = text[i]!;
    if (c === "`") {
      const end = text.indexOf("`", i + 1);
      if (end > i + 1) {
        push({ kind: "code", text: text.slice(i + 1, end) });
        i = end + 1;
        continue;
      }
    } else if (c === "*" || c === "_") {
      let n = 0;
      while (text[i + n] === c) n++;
      const end = n <= 3 && !WORD.test(text[i - 1] ?? "") && !/\s/.test(text[i + n] ?? " ") ? findClose(text, i + n, c, n) : -1;
      if (end > 0) {
        const inner = parseInline(text.slice(i + n, end));
        push(
          n === 1 ? { kind: "em", children: inner }
          : n === 2 ? { kind: "strong", children: inner }
          : { kind: "strong", children: [{ kind: "em", children: inner }] },
        );
        i = end + n;
        continue;
      }
      plain += c.repeat(n);
      i += n;
      continue;
    } else if (c === "[") {
      const m = LINK.exec(text.slice(i));
      if (m) {
        const label = parseInline(m[1]!);
        const href = safeHttpUrl(m[2]);
        if (!href) push(...label); // a link to anything but http(s) keeps only its label, as text
        else {
          // The destination host is always visible, so a label can't pass off one site as another.
          const host = new URL(href).host;
          push({ kind: "link", href, children: label });
          if (!inlineText(label).toLowerCase().includes(host.toLowerCase())) plain += ` (${host})`;
        }
        i += m[0].length;
        continue;
      }
    }
    plain += c;
    i++;
  }
  if (plain) out.push({ kind: "text", text: plain });
  return out;
}

/** The closing run of exactly n markers c: not after whitespace, not inside a longer run, not followed by a word. */
function findClose(text: string, from: number, c: string, n: number): number {
  const run = c.repeat(n);
  for (let j = text.indexOf(run, from + 1); j > 0; j = text.indexOf(run, j + 1)) {
    if (text[j - 1] === c || text[j + n] === c) continue;
    if (/\s/.test(text[j - 1]!) || WORD.test(text[j + n] ?? "")) continue;
    return j;
  }
  return -1;
}

function inlineText(nodes: Inline[]): string {
  return nodes.map((n) => (n.kind === "text" || n.kind === "code" ? n.text : inlineText(n.children))).join("");
}

/** Only absolute http(s) URLs are ever offered as a link. */
export function safeHttpUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const u = new URL(url);
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : null;
  } catch {
    return null;
  }
}
