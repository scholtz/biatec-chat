// Node 25 and later define a `localStorage` global of their own (undefined without --localstorage-file), which hides jsdom's.
// The tests expect a working Storage, as on Node 22 (CI): when the global is not one, put an in-memory Storage in its place.
class MemoryStorage implements Storage {
  private data = new Map<string, string>();
  get length(): number {
    return this.data.size;
  }
  clear(): void {
    this.data.clear();
  }
  getItem(key: string): string | null {
    return this.data.get(String(key)) ?? null;
  }
  key(index: number): string | null {
    return [...this.data.keys()][index] ?? null;
  }
  removeItem(key: string): void {
    this.data.delete(String(key));
  }
  setItem(key: string, value: string): void {
    this.data.set(String(key), String(value));
  }
  [name: string]: unknown;
}

for (const name of ["localStorage", "sessionStorage"] as const) {
  let working = false;
  try {
    // Reading the Node 25+ global can warn or throw; either way it is not a working Storage.
    working = typeof ((globalThis as Record<string, unknown>)[name] as Storage | undefined)?.clear === "function";
  } catch {
    working = false;
  }
  if (!working) Object.defineProperty(globalThis, name, { value: new MemoryStorage(), configurable: true, writable: true });
}
