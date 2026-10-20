import { defineComponent, h, type VNode, type VNodeArrayChildren } from "vue";
import { parseBlocks, type Block, type Inline, type ListBlock } from "./format";

/**
 * Renders reply text as the safe Markdown subset of {@link parseBlocks}. Every piece of model text becomes a text
 * node (never innerHTML), and links come only from safeHttpUrl.
 */
export default defineComponent({
  name: "MarkdownText",
  props: { text: { type: String, required: true } },
  setup(props) {
    return () => h("div", { class: "bc-text" }, parseBlocks(props.text).map(block));
  },
});

function block(b: Block): VNode {
  switch (b.kind) {
    case "p":
      return h("p", { class: "bc-para" }, inline(b.content));
    case "heading":
      return h(b.level <= 2 ? "h3" : "h4", { class: "bc-heading" }, inline(b.content));
    case "code":
      return h("pre", { class: "bc-code-block" }, [h("code", b.text)]);
    case "quote":
      return h("blockquote", { class: "bc-quote" }, inline(b.content));
    case "hr":
      return h("hr", { class: "bc-rule" });
    default:
      return list(b);
  }
}

function list(l: ListBlock): VNode {
  const items = l.items.map((it) => h("li", [...inline(it.content), ...(it.sublist ? [list(it.sublist)] : [])]));
  return l.kind === "ul"
    ? h("ul", { class: "bc-bullets" }, items)
    : h("ol", { class: "bc-numbered", start: l.start === 1 ? undefined : l.start }, items);
}

function inline(nodes: Inline[]): VNodeArrayChildren {
  return nodes.map((n) => {
    switch (n.kind) {
      case "text":
        return n.text;
      case "code":
        return h("code", { class: "bc-inline-code" }, n.text);
      case "strong":
        return h("strong", inline(n.children));
      case "em":
        return h("em", inline(n.children));
      case "link":
        return h("a", { class: "bc-link", href: n.href, target: "_blank", rel: "noopener noreferrer" }, inline(n.children));
    }
  });
}
