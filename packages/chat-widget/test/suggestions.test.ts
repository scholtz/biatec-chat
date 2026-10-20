import { describe, expect, it } from "vitest";
import { promptsFor } from "../src/components/chat/suggestions";

describe("promptsFor", () => {
  it("uses the built-in prompts when the tenant has none", () => {
    expect(promptsFor("Anonymous", "default", null)).toHaveLength(4);
    expect(promptsFor("Anonymous", "default", [])).toHaveLength(4);
    expect(promptsFor("Anonymous", "default")).toHaveLength(4);
  });

  it("shows the tenant's prompts, capped at four, to every tier", () => {
    const tenant = ["a", "b", "c", "d", "e"];
    for (const tier of ["Anonymous", "Authenticated"] as const) {
      const p = promptsFor(tier, "default", tenant);
      expect(p.map((x) => x.prompt)).toEqual(["a", "b", "c", "d"]);
    }
  });

  it("lets a host-supplied list win over the tenant's", () => {
    expect(promptsFor("Anonymous", ["host"], ["tenant"]).map((x) => x.prompt)).toEqual(["host"]);
  });
});
