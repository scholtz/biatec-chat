export type Block = { kind: "p"; text: string } | { kind: "ul"; items: string[] };

const BULLET = /^\s*(?:[-*•])\s+(.*)$/;

/**
 * Splits plain reply text into paragraphs and bullet lists. Pure text: nothing here is interpreted as markup or
 * HTML, and links stay text.
 */
export function parseBlocks(text: string): Block[] {
  const blocks: Block[] = [];
  let para: string[] = [];
  let list: string[] | null = null;
  const flushPara = () => {
    if (para.length) blocks.push({ kind: "p", text: para.join("\n") });
    para = [];
  };
  const flushList = () => {
    if (list?.length) blocks.push({ kind: "ul", items: list });
    list = null;
  };
  for (const line of text.replace(/\r\n?/g, "\n").split("\n")) {
    const bullet = BULLET.exec(line);
    if (bullet) {
      flushPara();
      (list ??= []).push(bullet[1]!.trim());
    } else if (!line.trim()) {
      flushPara();
      flushList();
    } else {
      flushList();
      para.push(line.trim());
    }
  }
  flushPara();
  flushList();
  return blocks;
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
