// T-250 spike: what do the TPM and the Windows key providers give a desktop authenticator?
// Build (x64 Native Tools prompt):  cl /nologo /EHsc /std:c++17 tpm_spike.cpp ncrypt.lib bcrypt.lib
// Run: tpm_spike.exe   (creates and deletes test keys named biatec-spike-*; no prompt is expected; touches nothing else)
#include <windows.h>
#include <bcrypt.h>
#include <ncrypt.h>
#include <cstdio>
#include <string>
#include <vector>

static void show(const char* what, SECURITY_STATUS s) { printf("  %-58s 0x%08lX%s\n", what, (unsigned long)s, s == 0 ? "  ok" : "  FAILED"); }

static void props(NCRYPT_KEY_HANDLE k) {
  DWORD impl = 0, n = 0, exp = 0, usage = 0;
  if (NCryptGetProperty(k, NCRYPT_IMPL_TYPE_PROPERTY, (PBYTE)&impl, 4, &n, 0) == 0)
    printf("  impl type: 0x%lX (hardware=%d software=%d removable=%d)\n", impl, !!(impl & NCRYPT_IMPL_HARDWARE_FLAG), !!(impl & NCRYPT_IMPL_SOFTWARE_FLAG), !!(impl & NCRYPT_IMPL_REMOVABLE_FLAG));
  if (NCryptGetProperty(k, NCRYPT_EXPORT_POLICY_PROPERTY, (PBYTE)&exp, 4, &n, 0) == 0) printf("  export policy: 0x%lX (0 = not exportable)\n", exp);
  if (NCryptGetProperty(k, NCRYPT_KEY_USAGE_PROPERTY, (PBYTE)&usage, 4, &n, 0) == 0) printf("  key usage: 0x%lX\n", usage);
}

static void tryProvider(const wchar_t* name, const wchar_t* alg, DWORD bits, const wchar_t* keyName) {
  wprintf(L"\n== %s, %s ==\n", name, alg);
  NCRYPT_PROV_HANDLE p = 0;
  SECURITY_STATUS s = NCryptOpenStorageProvider(&p, name, 0);
  show("open provider", s);
  if (s) return;
  NCRYPT_KEY_HANDLE k = 0;
  s = NCryptOpenKey(p, &k, keyName, 0, 0);
  if (s == 0) NCryptDeleteKey(k, 0);
  k = 0;
  s = NCryptCreatePersistedKey(p, &k, alg, keyName, 0, NCRYPT_OVERWRITE_KEY_FLAG);
  show("create persisted key", s);
  if (s) { NCryptFreeObject(p); return; }
  if (bits) { s = NCryptSetProperty(k, NCRYPT_LENGTH_PROPERTY, (PBYTE)&bits, 4, 0); show("set length", s); }
  s = NCryptFinalizeKey(k, 0);
  show("finalize key", s);
  if (s == 0) {
    props(k);
    DWORD n = 0;
    s = NCryptExportKey(k, 0, BCRYPT_PUBLIC_KEY_BLOB, nullptr, nullptr, 0, &n, 0);
    printf("  public blob size %lu\n", n);
    std::vector<BYTE> hash(32, 7);
    DWORD sig = 0;
    BCRYPT_PKCS1_PADDING_INFO pad = {BCRYPT_SHA256_ALGORITHM};
    bool rsa = wcscmp(alg, NCRYPT_RSA_ALGORITHM) == 0;
    s = NCryptSignHash(k, rsa ? &pad : nullptr, hash.data(), 32, nullptr, 0, &sig, rsa ? BCRYPT_PAD_PKCS1 : 0);
    show("sign (size query)", s);
    std::vector<BYTE> sg(sig ? sig : 1);
    LARGE_INTEGER a, b, f; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&a);
    s = NCryptSignHash(k, rsa ? &pad : nullptr, hash.data(), 32, sg.data(), sig, &sig, rsa ? BCRYPT_PAD_PKCS1 : 0);
    QueryPerformanceCounter(&b);
    show("sign", s);
    printf("  signature %lu bytes in %.0f ms\n", sig, 1000.0 * (b.QuadPart - a.QuadPart) / f.QuadPart);
    DWORD exp = 0;
    s = NCryptExportKey(k, 0, NCRYPT_PKCS8_PRIVATE_KEY_BLOB, nullptr, nullptr, 0, &n, 0);
    show("export PRIVATE key (must FAIL)", s);
    NCryptDeleteKey(k, 0);
  } else NCryptFreeObject(k);
  NCryptFreeObject(p);
}

int wmain() {
  tryProvider(MS_PLATFORM_CRYPTO_PROVIDER, NCRYPT_ECDSA_P256_ALGORITHM, 0, L"biatec-spike-presence");
  tryProvider(MS_PLATFORM_CRYPTO_PROVIDER, NCRYPT_RSA_ALGORITHM, 2048, L"biatec-spike-rsa");
  tryProvider(MS_KEY_STORAGE_PROVIDER, NCRYPT_ECDSA_P256_ALGORITHM, 0, L"biatec-spike-software");
  tryProvider(MS_NGC_KEY_STORAGE_PROVIDER, NCRYPT_ECDSA_P256_ALGORITHM, 0, L"biatec-spike-ngc-p256");
  return 0;
}
