/**
 * Literal fallbacks for the exported chip, which hosts mount outside the widget root where the design tokens in
 * tokens.css are undefined. Same values as the brand tokens.
 */
export const chipVar = {
  primary: "var(--color-brand-primary, #2dd4a7)",
  on: "var(--color-brand-on, #04130e)",
  pins: "var(--color-text-secondary, #9aa7ba)",
} as const;
