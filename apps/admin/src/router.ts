import { computed, ref } from "vue";

export type RouteName = "overview" | "users" | "user" | "view";
export interface Route { name: RouteName; query: URLSearchParams }

export function parseHash(hash: string): Route {
  const raw = hash.replace(/^#/, "");
  const i = raw.indexOf("?");
  const path = i < 0 ? raw : raw.slice(0, i);
  const query = new URLSearchParams(i < 0 ? "" : raw.slice(i + 1));
  const seg = path.replace(/^\/+|\/+$/g, "");
  const name: RouteName = seg === "users" ? "users" : seg === "user" ? "user" : seg === "view" ? "view" : "overview";
  return { name, query };
}

const hash = ref(typeof location === "undefined" ? "" : location.hash);
if (typeof window !== "undefined") {
  window.addEventListener("hashchange", () => (hash.value = location.hash));
}

export const route = computed(() => parseHash(hash.value));

export function navigate(path: string) {
  location.hash = path;
  hash.value = location.hash;
}

export function userHref(tenant: string, sub: string): string {
  return `#/user?${new URLSearchParams({ tenant, sub })}`;
}
