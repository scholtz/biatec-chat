import { describe, it, expect } from "vitest";
import { mount } from "@vue/test-utils";
import MarkdownText from "../src/components/chat/MarkdownText";
import { parseBlocks, parseInline, inlineText } from "../src/components/chat/format";

const render = (text: string) => mount(MarkdownText, { props: { text } });

describe("inline Markdown", () => {
  it("parses strong, em, code and links", () => {
    expect(parseInline("a **b** *c* `d` [e](https://biatec.io)")).toEqual([
      { kind: "text", text: "a " },
      { kind: "strong", children: [{ kind: "text", text: "b" }] },
      { kind: "text", text: " " },
      { kind: "em", children: [{ kind: "text", text: "c" }] },
      { kind: "text", text: " " },
      { kind: "code", text: "d" },
      { kind: "text", text: " " },
      { kind: "link", href: "https://biatec.io/", children: [{ kind: "text", text: "e" }] },
    ]);
  });

  it("keeps unclosed markers as literal text, as mid-stream", () => {
    expect(parseInline("1. **How much EUR")).toEqual([{ kind: "text", text: "1. **How much EUR" }]);
    expect(parseInline("price `1.2")).toEqual([{ kind: "text", text: "price `1.2" }]);
    expect(parseInline("a * b * c")).toEqual([{ kind: "text", text: "a * b * c" }]);
  });

  it("leaves snake_case and asset ids alone", () => {
    expect(inlineText(parseInline("pool_app_id and snake_case_name"))).toBe("pool_app_id and snake_case_name");
    expect(parseInline("pool_app_id")).toEqual([{ kind: "text", text: "pool_app_id" }]);
  });

  it("drops non-http(s) link targets and keeps the label as text", () => {
    expect(parseInline("[x](javascript:alert(1))")).toEqual([{ kind: "text", text: "x" }]);
    expect(parseInline("[x](data:text/html,hi)")).toEqual([{ kind: "text", text: "x" }]);
  });
});

describe("block Markdown", () => {
  it("parses headings, ordered lists with a start, nested lists, code, quotes and rules", () => {
    const blocks = parseBlocks("# Title\n\n3. three\n4. four\n   - sub\n\n```js\nlet a = 1;\n```\n> quoted\n\n---");
    expect(blocks.map((b) => b.kind)).toEqual(["heading", "ol", "code", "quote", "hr"]);
    const ol = blocks[1]!;
    expect(ol.kind === "ol" && ol.start).toBe(3);
    expect(ol.kind === "ol" && ol.items[1]!.sublist?.kind).toBe("ul");
    expect(blocks[2]).toEqual({ kind: "code", text: "let a = 1;" });
  });

  it("runs an unclosed code fence to the end of the text", () => {
    expect(parseBlocks("Here:\n```\nline 1\nline 2")).toEqual([
      { kind: "p", content: [{ kind: "text", text: "Here:" }] },
      { kind: "code", text: "line 1\nline 2" },
    ]);
  });
});

describe("MarkdownText rendering", () => {
  it("renders the demo reply with bold numbered items and no raw asterisks", () => {
    const w = render(
      "To add liquidity, I need:\n\n1. **How much EURS** do you want to deposit?\n2. **How much USDC** do you want to deposit?\n\n(EURS has 6 decimals.)",
    );
    const items = w.findAll("ol.bc-numbered > li");
    expect(items.map((li) => li.text())).toEqual(["How much EURS do you want to deposit?", "How much USDC do you want to deposit?"]);
    expect(items[0]!.get("strong").text()).toBe("How much EURS");
    expect(w.text()).not.toContain("**");
  });

  it("renders headings, italics, inline and block code", () => {
    const w = render("## Pools\n\n*fee* `0.1%`\n\n```\n{\"a\":1}\n```");
    expect(w.get("h3.bc-heading").text()).toBe("Pools");
    expect(w.get("em").text()).toBe("fee");
    expect(w.get("code.bc-inline-code").text()).toBe("0.1%");
    expect(w.get("pre.bc-code-block code").text()).toBe('{"a":1}');
  });

  it("shows HTML in model text as literal text, never as elements", () => {
    const w = render('<script>alert(1)</script> <img src=x onerror="alert(1)"> **<b>bold</b>**\n\n- <i>item</i>');
    expect(w.find("script").exists()).toBe(false);
    expect(w.find("img").exists()).toBe(false);
    expect(w.find("b").exists()).toBe(false);
    expect(w.find("i").exists()).toBe(false);
    expect(w.get(".bc-para").text()).toContain("<script>alert(1)</script>");
    expect(w.get("strong").text()).toBe("<b>bold</b>");
    expect(w.get(".bc-bullets li").text()).toBe("<i>item</i>");
  });

  it("links only http(s) targets, opening in a new tab without an opener", () => {
    const w = render("[safe](https://biatec.io) and [bad](javascript:alert(1))");
    const links = w.findAll("a");
    expect(links).toHaveLength(1);
    expect(links[0]!.attributes()).toMatchObject({ href: "https://biatec.io/", target: "_blank", rel: "noopener noreferrer" });
    expect(w.text()).toBe("safe and bad");
  });

  it("re-renders cleanly as a reply streams in", async () => {
    const w = render("1. **How");
    expect(w.get("ol li").text()).toBe("**How");
    await w.setProps({ text: "1. **How much** EURS?" });
    expect(w.get("ol li strong").text()).toBe("How much");
  });
});
