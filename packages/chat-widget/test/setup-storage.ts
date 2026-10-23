// Node 25+ defines a global `localStorage`/`sessionStorage` that is unusable without --localstorage-file (reading it warns and yields
// undefined) and takes the place of jsdom's. Tests that clear or fill the storage need a working one on every Node version, so a
// missing or broken one is replaced by an in-memory Storage.
class MemoryStorage implements Storage {
  private map = new Map<string, string>();
  get length(): number { return this.map.size; }
  clear(): void { this.map.clear(); }
  getItem(key: string): string | null { return this.map.get(String(key)) ?? null; }
  key(index: number): string | null { return [...this.map.keys()][index] ?? null; }
  removeItem(key: string): void { this.map.delete(String(key)); }
  setItem(key: string, value: string): void { this.map.set(String(key), String(value)); }
  [name: string]: unknown;
}

for (const name of ["localStorage", "sessionStorage"] as const) {
  let usable = false;
  try { usable = typeof (globalThis as Record<string, unknown> & { [k: string]: Storage | undefined })[name]?.clear === "function"; } catch { /* the getter throws */ }
  if (!usable) Object.defineProperty(globalThis, name, { value: new MemoryStorage(), configurable: true, writable: true });
}
