import { ref, shallowRef } from "vue";

/** Async data holder with loading/error state; a stale response (an older call finishing late) is dropped. */
export function useLoader<T>(fn: () => Promise<T>) {
  const data = shallowRef<T | null>(null);
  const loading = ref(true);
  const error = ref("");
  let seq = 0;

  async function load() {
    const mine = ++seq;
    loading.value = true;
    error.value = "";
    try {
      const r = await fn();
      if (mine === seq) data.value = r;
    } catch (e) {
      if (mine === seq) error.value = e instanceof Error ? e.message : "Something went wrong.";
    } finally {
      if (mine === seq) loading.value = false;
    }
  }
  return { data, loading, error, load };
}

/** localStorage for UI preferences only; never throws. */
export function readPref(key: string): string | null {
  try {
    return localStorage.getItem(`biatec-admin:${key}`);
  } catch {
    return null;
  }
}
export function writePref(key: string, value: string) {
  try {
    localStorage.setItem(`biatec-admin:${key}`, value);
  } catch { /* storage blocked */ }
}
