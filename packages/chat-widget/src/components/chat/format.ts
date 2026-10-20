/** Inline Markdown: plain text, `code`, **strong**, *em* and links (http(s) only). */
export type Inline =
  | { kind: "text"; text: string }
  | { kind: "code"; text: string }
  | { kind: "strong"; children: Inline[] }
  | { kind: "em"; children: Inline[] }
  | { kind: "link"; href: string; children: Inline[] };

export type ListItem = { content: Inline[]; sublist?: ListBlock };
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
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
const FENCE = /^\s{0,3}(```|~~~)/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const QUOTE = /^\s{0,3}>\s?(.*)$/;

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
  let list: ListBlock | null = null;
  let listIndent = 0;

  const flushPara = () => {
    if (para.length) blocks.push({ kind: "p", content: parseInline(para.join("\n")) });
    para = [];
  };
  const flushQuote = () => {
    if (quote.length) blocks.push({ kind: "quote", content: parseInline(quote.join("\n")) });
    quote = [];
  };
  const flushList = () => {
    if (list) blocks.push(list);
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
      flushAll();
      continue;
    }
    const item = listItem(line);
    if (item) {
      flushPara();
      flushQuote();
      const current = list as ListBlock | null;
      const last = current?.items[current.items.length - 1];
      if (current && last && item.indent > listIndent) {
        // Deeper than the list's own markers: one nesting level under the previous item.
        if (!last.sublist || last.sublist.kind !== item.list.kind) last.sublist = item.list;
        else last.sublist.items.push(...item.list.items);
      } else if (current && current.kind === item.list.kind) {
        current.items.push(...item.list.items);
      } else {
        flushList();
        list = item.list;
        listIndent = item.indent;
      }
      continue;
    }
    const current = list as ListBlock | null;
    if (current && /^\s+/.test(line)) {
      // An indented line continues the previous item.
      const last = current.items[current.items.length - 1]!;
      const target = last.sublist ? last.sublist.items[last.sublist.items.length - 1]! : last;
      target.content.push({ kind: "text", text: "\n" }, ...parseInline(line.trim()));
      continue;
    }
    const heading = HEADING.exec(line);
    if (heading) {
      flushAll();
      blocks.push({ kind: "heading", level: heading[1]!.length, content: parseInline(heading[2]!) });
      continue;
    }
    if (RULE.test(line)) {
      flushAll();
      blocks.push({ kind: "hr" });
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

function listItem(line: string): { indent: number; list: ListBlock } | null {
  const b = BULLET.exec(line);
  if (b) return { indent: b[1]!.length, list: { kind: "ul", items: [{ content: parseInline(b[2]!.trim()) }] } };
  const o = ORDERED.exec(line);
  if (o) return { indent: o[1]!.length, list: { kind: "ol", start: Number(o[2]), items: [{ content: parseInline(o[3]!.trim()) }] } };
  return null;
}

// The target may hold one level of balanced parentheses, as in https://en.wikipedia.org/wiki/Foo_(bar).
const LINK = /^\[([^\]\n]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)/;

/** Inline spans. An opening marker without its closing one stays literal text, so partial text streams cleanly. */
export function parseInline(text: string): Inline[] {
  const out: Inline[] = [];
  let plain = "";
  const push = (node: Inline) => {
    if (plain) out.push({ kind: "text", text: plain });
    plain = "";
    out.push(node);
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
    } else if ((c === "*" || c === "_") && text[i + 1] === c) {
      const end = text.indexOf(c + c, i + 2);
      if (end > i + 2 && !/\s/.test(text[i + 2]!)) {
        push({ kind: "strong", children: parseInline(text.slice(i + 2, end)) });
        i = end + 2;
        continue;
      }
    } else if ((c === "*" || c === "_") && !(c === "_" && /\w/.test(text[i - 1] ?? ""))) {
      // Single marker: emphasis. "_" inside a word (snake_case) is not a marker.
      const end = findEmClose(text, i + 1, c);
      if (end > i + 1 && !/\s/.test(text[i + 1]!)) {
        push({ kind: "em", children: parseInline(text.slice(i + 1, end)) });
        i = end + 1;
        continue;
      }
    } else if (c === "[") {
      const m = LINK.exec(text.slice(i));
      if (m) {
        const href = safeHttpUrl(m[2]);
        // A link to anything but http(s) keeps only its label, as text.
        if (href) push({ kind: "link", href, children: parseInline(m[1]!) });
        else plain += m[1];
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

function findEmClose(text: string, from: number, marker: string): number {
  for (let j = from; j < text.length; j++) {
    if (text[j] === "\n") return -1;
    if (text[j] !== marker) continue;
    if (text[j + 1] === marker) {
      j++; // part of a ** pair, not this emphasis
      continue;
    }
    if (/\s/.test(text[j - 1]!)) continue;
    if (marker === "_" && /\w/.test(text[j + 1] ?? "")) continue;
    return j;
  }
  return -1;
}

/** The plain text of inline spans, for tests and accessible labels. */
export function inlineText(nodes: Inline[]): string {
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
