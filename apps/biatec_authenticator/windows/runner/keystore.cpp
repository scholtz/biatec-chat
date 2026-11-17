#include "keystore.h"

#include <windows.h>
#include <bcrypt.h>
#include <dpapi.h>
#include <ncrypt.h>

#include <atomic>
#include <cstring>
#include <cwchar>
#include <mutex>

namespace keystore {
namespace {

// Names of the keys in a provider (per Windows user). The version leaves room to replace them.
constexpr wchar_t kApprovalName[] = L"io.biatec.authenticator.approval.v1";
constexpr wchar_t kPresenceName[] = L"io.biatec.authenticator.presence.v1";
// The key that says this enrollment uses the app's PIN (only on a computer without Windows Hello). It signs nothing that leaves the computer. In the TPM it
// carries the PIN: a signature with it says the PIN is right.
constexpr wchar_t kLockName[] = L"io.biatec.authenticator.lock.v1";

// NTE_BAD_KEYSET, NTE_NO_KEY and NTE_NOT_FOUND: the key is not there (providers differ in which of them they answer for a name that was never made).
constexpr SECURITY_STATUS kBadKeyset = static_cast<SECURITY_STATUS>(0x80090016);
constexpr SECURITY_STATUS kNoKey = static_cast<SECURITY_STATUS>(0x8009000D);
constexpr SECURITY_STATUS kNotFound = static_cast<SECURITY_STATUS>(0x80090011);

// The TPM is locked after too many wrong tries: TPM_20_E_LOCKOUT, TPM_E_DEFEND_LOCK_RUNNING, and SCARD_W_CHV_BLOCKED (how a provider may say it).
constexpr SECURITY_STATUS kTpmLockout = static_cast<SECURITY_STATUS>(0x80280921);
constexpr SECURITY_STATUS kTpmDefendLock = static_cast<SECURITY_STATUS>(0x80280803);
constexpr SECURITY_STATUS kPinBlocked = static_cast<SECURITY_STATUS>(0x8010006C);

bool IsLockout(SECURITY_STATUS status) {
  return status == kTpmLockout || status == kTpmDefendLock || status == kPinBlocked;
}

constexpr size_t kMaxSignedBytes = 1 << 20;

// One operation on the keys at a time: a delete, a create and a sign from different worker threads must not interleave.
std::mutex g_keys;

const wchar_t* NameOf(Use use) {
  switch (use) {
    case Use::kApproval:
      return kApprovalName;
    case Use::kLock:
      return kLockName;
    default:
      return kPresenceName;
  }
}

// Owns a CNG handle (a provider or a key) and frees it.
class Handle {
 public:
  Handle() = default;
  Handle(const Handle&) = delete;
  Handle& operator=(const Handle&) = delete;
  Handle(Handle&& other) noexcept : h_(other.h_) { other.h_ = 0; }
  Handle& operator=(Handle&& other) noexcept {
    if (this != &other) {
      if (h_ != 0) NCryptFreeObject(h_);
      h_ = other.h_;
      other.h_ = 0;
    }
    return *this;
  }
  ~Handle() {
    if (h_ != 0) NCryptFreeObject(h_);
  }
  ULONG_PTR* put() { return &h_; }
  ULONG_PTR get() const { return h_; }
  bool open() const { return h_ != 0; }
  // Gives the handle away (NCryptDeleteKey frees it itself).
  ULONG_PTR release() {
    ULONG_PTR h = h_;
    h_ = 0;
    return h;
  }

 private:
  ULONG_PTR h_ = 0;
};

// The provider of a store, or a handle that is not open when the provider cannot be opened (a computer without a TPM has no Platform Crypto Provider to open).
Handle OpenStore(Store store) {
  Handle provider;
  NCryptOpenStorageProvider(
      provider.put(),
      store == Store::kTpm ? MS_PLATFORM_CRYPTO_PROVIDER : MS_KEY_STORAGE_PROVIDER, 0);
  return provider;
}

// Opens the named key. Returns false when it is not there; any other failure throws.
bool OpenKey(const Handle& provider, Use use, Handle& key) {
  const SECURITY_STATUS status =
      NCryptOpenKey(provider.get(), key.put(), NameOf(use), 0, NCRYPT_SILENT_FLAG);
  if (status == ERROR_SUCCESS) return true;
  if (status == kBadKeyset || status == kNoKey || status == kNotFound) return false;
  throw KeyError("key_failed", "The key cannot be opened (status " +
                                   std::to_string(static_cast<long>(status)) + ").");
}

// A key that is there, with the store and the provider it was found in.
struct Found {
  Store store = Store::kTpm;
  Handle provider;
  Handle key;
};

// Looks for the key in the TPM, then in the software store. False when it is in neither. Throws when no store can be opened at all.
bool Find(Use use, Found& found) {
  bool any = false;
  for (const Store store : {Store::kTpm, Store::kSoftware}) {
    Handle provider = OpenStore(store);
    if (!provider.open()) continue;
    any = true;
    Handle key;
    if (!OpenKey(provider, use, key)) continue;
    found.store = store;
    found.key = std::move(key);
    found.provider = std::move(provider);
    return true;
  }
  if (!any) throw KeyError("key_failed", "No key store of Windows can be opened.");
  return false;
}

bool DwordProperty(const Handle& object, LPCWSTR name, DWORD* value) {
  DWORD size = 0;
  return NCryptGetProperty(object.get(), name, reinterpret_cast<PBYTE>(value),
                           sizeof(DWORD), &size, 0) == ERROR_SUCCESS &&
         size == sizeof(DWORD);
}

// True when the provider is hardware. This is a property of the provider: asked of a key, Windows answers "not supported" (seen on a real TPM), so the key is
// asked first only for a provider that does answer there.
bool ProviderIsHardware(const Handle& provider, const Handle* key = nullptr) {
  DWORD impl = 0;
  if (key != nullptr && DwordProperty(*key, NCRYPT_IMPL_TYPE_PROPERTY, &impl)) {
    return (impl & NCRYPT_IMPL_HARDWARE_FLAG) != 0;
  }
  if (!DwordProperty(provider, NCRYPT_IMPL_TYPE_PROPERTY, &impl)) return false;
  return (impl & NCRYPT_IMPL_HARDWARE_FLAG) != 0;
}

// True when the key is held by hardware and cannot be exported: the provider says hardware, and the key's export policy allows nothing. A provider that does
// not answer the export query is not held against the key (every key of the Platform Crypto Provider is made inside the TPM and cannot leave it). Step 4 of
// the manual checklist looks at the real keys.
bool InHardware(const Handle& provider, const Handle& key) {
  if (!ProviderIsHardware(provider, &key)) return false;
  DWORD exportPolicy = 0;
  if (DwordProperty(key, NCRYPT_EXPORT_POLICY_PROPERTY, &exportPolicy) &&
      exportPolicy != 0) {
    return false;
  }
  return true;
}

PublicKey Describe(Store store, const Handle& provider, const Handle& key) {
  DWORD size = 0;
  SECURITY_STATUS status = NCryptExportKey(key.get(), 0, BCRYPT_ECCPUBLIC_BLOB,
                                           nullptr, nullptr, 0, &size, 0);
  if (status != ERROR_SUCCESS || size < sizeof(BCRYPT_ECCKEY_BLOB)) {
    throw KeyError("key_failed", "The public key cannot be read.");
  }
  std::vector<uint8_t> blob(size);
  status = NCryptExportKey(key.get(), 0, BCRYPT_ECCPUBLIC_BLOB, nullptr,
                           blob.data(), size, &size, 0);
  if (status != ERROR_SUCCESS) {
    throw KeyError("key_failed", "The public key cannot be read.");
  }
  BCRYPT_ECCKEY_BLOB header;
  std::memcpy(&header, blob.data(), sizeof(header));
  // A P-256 public key: the magic of an ECDSA P-256 public blob and 32-byte coordinates.
  if (header.dwMagic != BCRYPT_ECDSA_PUBLIC_P256_MAGIC || header.cbKey != 32 ||
      blob.size() < sizeof(header) + 64) {
    throw KeyError("key_failed", "The key is not a P-256 key.");
  }
  const uint8_t* coordinates = blob.data() + sizeof(header);
  PublicKey key_out;
  key_out.x = Base64Url(coordinates, 32);
  key_out.y = Base64Url(coordinates + 32, 32);
  key_out.store = store;
  key_out.hardware = store == Store::kTpm && InHardware(provider, key);
  return key_out;
}

// Gives the key handle the PIN: before the key is finalized it becomes the key's usage secret, on an open key it is what the TPM checks at the signature.
SECURITY_STATUS SetPin(const Handle& key, const std::wstring& pin) {
  return NCryptSetProperty(
      key.get(), NCRYPT_PIN_PROPERTY,
      reinterpret_cast<PBYTE>(const_cast<wchar_t*>(pin.c_str())),
      static_cast<DWORD>((pin.size() + 1) * sizeof(wchar_t)), 0);
}

// ECDSA P-256 over a SHA-256 `digest`. Returns the status of the signature; `signature` is the 64-byte `r || s` on success.
SECURITY_STATUS SignDigest(const Handle& key, const uint8_t* digest, const std::wstring* pin,
                           std::vector<uint8_t>* signature) {
  if (pin != nullptr) {
    const SECURITY_STATUS set = SetPin(key, *pin);
    if (set != ERROR_SUCCESS) return set;
  }
  signature->assign(64, 0);
  DWORD written = 0;
  // Always silent: a refused PIN, or a key that wants one, is an answer, never a dialog of the provider.
  const SECURITY_STATUS status = NCryptSignHash(
      key.get(), nullptr, const_cast<PBYTE>(digest), 32, signature->data(),
      static_cast<DWORD>(signature->size()), &written, NCRYPT_SILENT_FLAG);
  // ECDSA P-256 in CNG answers the IEEE P1363 form already: 32 bytes of r, then 32 bytes of s.
  if (status == ERROR_SUCCESS && written != 64) return NTE_FAIL;
  return status;
}

// Makes a P-256 signing key in `provider`. `name` null makes a key that is not kept (the test of the TPM).
SECURITY_STATUS MakeKey(const Handle& provider, const wchar_t* name, const std::wstring* pin,
                        Handle& key) {
  // Overwrites a key of the same name. The old keys were deleted before (FreshKeys); what can still be there is a key the TPM can no longer open (the TPM was
  // cleared or replaced), which reads as "not there" and would otherwise refuse every later enrollment.
  SECURITY_STATUS status = NCryptCreatePersistedKey(
      provider.get(), key.put(), BCRYPT_ECDSA_P256_ALGORITHM, name, 0,
      name == nullptr ? 0 : NCRYPT_OVERWRITE_KEY_FLAG);
  if (status != ERROR_SUCCESS) return status;
  // For signing only, and never exportable: the policy is set to "nothing" (the default of both providers, stated here so that it does not depend on it).
  DWORD usage = NCRYPT_ALLOW_SIGNING_FLAG;
  status = NCryptSetProperty(key.get(), NCRYPT_KEY_USAGE_PROPERTY,
                             reinterpret_cast<PBYTE>(&usage), sizeof(usage), 0);
  if (status != ERROR_SUCCESS) return status;
  DWORD no_export = 0;
  // Not every provider takes this property before the key is finalized; the key is read back after (InHardware, and the software key below).
  NCryptSetProperty(key.get(), NCRYPT_EXPORT_POLICY_PROPERTY,
                    reinterpret_cast<PBYTE>(&no_export), sizeof(no_export), 0);
  if (pin != nullptr) {
    status = SetPin(key, *pin);
    if (status != ERROR_SUCCESS) return status;
  }
  return NCryptFinalizeKey(key.get(), NCRYPT_SILENT_FLAG);
}

void Create(const Handle& provider, Use use, const std::wstring* pin) {
  Handle key;
  const SECURITY_STATUS status = MakeKey(provider, NameOf(use), pin, key);
  if (status != ERROR_SUCCESS) {
    throw KeyError("key_generation_failed",
                   "The key cannot be created (status " +
                       std::to_string(static_cast<long>(status)) + ").");
  }
}

// --- The PIN of the software store -----------------------------------------------------------------------------------------------------------------------
//
// The software provider cannot hold a PIN the way the TPM does (it takes one only together with a dialog of its own). What is kept instead: a random salt, a
// PBKDF2-HMAC-SHA256 hash of the PIN, and the count and time of the wrong tries, in one small file that Windows encrypts for this Windows account (DPAPI).

constexpr ULONG kPinIterations = 600000;
constexpr DWORD kPinFileVersion = 1;
// After this many wrong tries in a row the PIN is refused for a while: 30 seconds, doubled with every further wrong try, up to an hour.
constexpr DWORD kFreeTries = 5;
constexpr ULONGLONG kFirstWaitSeconds = 30;
constexpr ULONGLONG kLongestWaitSeconds = 3600;

#pragma pack(push, 1)
struct PinRecord {
  DWORD version;
  uint8_t salt[16];
  uint8_t hash[32];
  DWORD failures;
  ULONGLONG last_failure;  // FILETIME, 100 ns since 1601 (UTC)
};
#pragma pack(pop)

// %LOCALAPPDATA%\io.biatec.authenticator\pin.v1, or empty when the folder is not known.
std::wstring PinPath(bool make_folder) {
  wchar_t base[MAX_PATH];
  const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) return L"";
  std::wstring folder = std::wstring(base) + L"\\io.biatec.authenticator";
  if (make_folder) CreateDirectoryW(folder.c_str(), nullptr);
  return folder + L"\\pin.v1";
}

ULONGLONG NowFileTime() {
  FILETIME now;
  GetSystemTimeAsFileTime(&now);
  return (static_cast<ULONGLONG>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
}

bool PinHash(const std::wstring& pin, const uint8_t* salt, uint8_t* hash) {
  return BCryptDeriveKeyPBKDF2(
             BCRYPT_HMAC_SHA256_ALG_HANDLE,
             reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(pin.data())),
             static_cast<ULONG>(pin.size() * sizeof(wchar_t)),
             const_cast<PUCHAR>(salt), 16, kPinIterations, hash, 32, 0) == 0;
}

bool WritePinRecord(const PinRecord& record) {
  const std::wstring path = PinPath(true);
  if (path.empty()) return false;
  DATA_BLOB plain;
  plain.pbData = const_cast<BYTE*>(reinterpret_cast<const BYTE*>(&record));
  plain.cbData = sizeof(record);
  DATA_BLOB sealed = {};
  if (!CryptProtectData(&plain, L"Biatec Authenticator PIN", nullptr, nullptr, nullptr,
                        CRYPTPROTECT_UI_FORBIDDEN, &sealed)) {
    return false;
  }
  // Written beside the file and moved over it: a write that is cut off never leaves half a record.
  const std::wstring next = path + L".new";
  bool ok = false;
  const HANDLE file = CreateFileW(next.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file != INVALID_HANDLE_VALUE) {
    DWORD written = 0;
    ok = WriteFile(file, sealed.pbData, sealed.cbData, &written, nullptr) &&
         written == sealed.cbData && FlushFileBuffers(file);
    CloseHandle(file);
  }
  LocalFree(sealed.pbData);
  if (ok) ok = MoveFileExW(next.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
  if (!ok) DeleteFileW(next.c_str());
  return ok;
}

bool ReadPinRecord(PinRecord* record) {
  const std::wstring path = PinPath(false);
  if (path.empty()) return false;
  const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  BYTE buffer[4096];
  DWORD read = 0;
  const bool got = ReadFile(file, buffer, sizeof(buffer), &read, nullptr) && read > 0;
  CloseHandle(file);
  if (!got) return false;
  DATA_BLOB sealed;
  sealed.pbData = buffer;
  sealed.cbData = read;
  DATA_BLOB plain = {};
  if (!CryptUnprotectData(&sealed, nullptr, nullptr, nullptr, nullptr,
                          CRYPTPROTECT_UI_FORBIDDEN, &plain)) {
    return false;
  }
  const bool ok = plain.cbData == sizeof(PinRecord);
  if (ok) std::memcpy(record, plain.pbData, sizeof(PinRecord));
  SecureZeroMemory(plain.pbData, plain.cbData);
  LocalFree(plain.pbData);
  return ok && record->version == kPinFileVersion;
}

void DeletePinRecord() {
  const std::wstring path = PinPath(false);
  if (path.empty()) return;
  DeleteFileW(path.c_str());
  DeleteFileW((path + L".new").c_str());
}

// Keeps the hash of a new PIN. Throws when it cannot be kept: an enrollment whose PIN cannot be checked is not made.
void KeepPin(const std::wstring& pin) {
  PinRecord record = {};
  record.version = kPinFileVersion;
  if (BCryptGenRandom(nullptr, record.salt, sizeof(record.salt),
                      BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0 ||
      !PinHash(pin, record.salt, record.hash) || !WritePinRecord(record)) {
    throw KeyError("key_generation_failed", "The PIN cannot be kept.");
  }
}

// How long a PIN is refused after `failures` wrong tries in a row, in seconds.
ULONGLONG WaitSeconds(DWORD failures) {
  if (failures < kFreeTries) return 0;
  ULONGLONG wait = kFirstWaitSeconds;
  for (DWORD i = kFreeTries; i < failures && wait < kLongestWaitSeconds; i++) wait *= 2;
  return wait < kLongestWaitSeconds ? wait : kLongestWaitSeconds;
}

PinCheck CheckKeptPin(const std::wstring& pin) {
  PinRecord record;
  // No record, or one that cannot be read: nothing to check against, so nothing is accepted.
  if (!ReadPinRecord(&record)) return PinCheck::kRefused;
  const ULONGLONG now = NowFileTime();
  const ULONGLONG wait = WaitSeconds(record.failures) * 10000000ULL;
  // A clock that was set back does not shorten the wait.
  if (wait != 0 && (now < record.last_failure || now - record.last_failure < wait)) {
    return PinCheck::kLocked;
  }
  uint8_t hash[32];
  if (!PinHash(pin, record.salt, hash)) return PinCheck::kRefused;
  // Every byte is compared, whatever the first difference.
  uint8_t difference = 0;
  for (size_t i = 0; i < sizeof(hash); i++) difference |= hash[i] ^ record.hash[i];
  SecureZeroMemory(hash, sizeof(hash));
  if (difference == 0) {
    if (record.failures != 0) {
      record.failures = 0;
      record.last_failure = 0;
      WritePinRecord(record);
    }
    return PinCheck::kAccepted;
  }
  record.failures++;
  record.last_failure = now;
  WritePinRecord(record);
  return PinCheck::kRefused;
}

// The TPM made a P-256 key in this process before: it is not asked again.
std::atomic<bool> g_tpm_made_key{false};

}  // namespace

std::string Base64Url(const uint8_t* data, size_t size) {
  static const char kAlphabet[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  std::string out;
  out.reserve((size + 2) / 3 * 4);
  size_t i = 0;
  for (; i + 2 < size; i += 3) {
    const uint32_t v = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
    out += kAlphabet[(v >> 18) & 63];
    out += kAlphabet[(v >> 12) & 63];
    out += kAlphabet[(v >> 6) & 63];
    out += kAlphabet[v & 63];
  }
  if (size - i == 1) {
    const uint32_t v = data[i] << 16;
    out += kAlphabet[(v >> 18) & 63];
    out += kAlphabet[(v >> 12) & 63];
  } else if (size - i == 2) {
    const uint32_t v = (data[i] << 16) | (data[i + 1] << 8);
    out += kAlphabet[(v >> 18) & 63];
    out += kAlphabet[(v >> 12) & 63];
    out += kAlphabet[(v >> 6) & 63];
  }
  return out;
}

bool TpmAvailable() {
#ifdef BIATEC_KEYSTORE_WITHOUT_TPM
  // For the test program of this file only (never defined in a build of the app): the software store on a computer that has a TPM.
  return false;
#else
  if (g_tpm_made_key) return true;
  const Handle provider = OpenStore(Store::kTpm);
  if (!provider.open() || !ProviderIsHardware(provider)) return false;
  // The real question is whether it makes the key (a TPM 1.2, or a restricted firmware TPM, opens the provider and cannot). A key without a name is made and
  // not kept.
  Handle key;
  if (MakeKey(provider, nullptr, nullptr, key) != ERROR_SUCCESS) return false;
  g_tpm_made_key = true;
  return true;
#endif
}

std::optional<PublicKey> Load(Use use) {
  std::lock_guard<std::mutex> lock(g_keys);
  Found found;
  if (!Find(use, found)) return std::nullopt;
  return Describe(found.store, found.provider, found.key);
}

namespace {

// Deletes a key that is there. A key that cannot be deleted is an error: it would be found again and handed to the next enrollment.
void DeleteKey(const Handle& provider, Use use) {
  Handle key;
  if (!OpenKey(provider, use, key)) return;
  // Frees the handle itself, so it is given away first.
  const SECURITY_STATUS status = NCryptDeleteKey(key.release(), 0);
  if (status != ERROR_SUCCESS) {
    throw KeyError("key_failed", "The old key cannot be deleted (status " +
                                     std::to_string(static_cast<long>(status)) + ").");
  }
}

// Every key in both stores, and the kept hash of the PIN. Every key is tried, and the first failure is reported after.
void DeleteEverywhere() {
  std::optional<KeyError> failure;
  for (const Store store : {Store::kTpm, Store::kSoftware}) {
    const Handle provider = OpenStore(store);
    if (!provider.open()) continue;  // no such store: no keys of ours in it
    for (const Use use : {Use::kApproval, Use::kPresence, Use::kLock}) {
      try {
        DeleteKey(provider, use);
      } catch (const KeyError& e) {
        if (!failure) failure = e;
      }
    }
  }
  DeletePinRecord();
  if (failure) throw *failure;
}

// Makes the key and reads it back. A key of the TPM that is not reported as hardware is refused; a key of the software store must at least not be exportable.
PublicKey CreateAndDescribe(Store store, const Handle& provider, Use use,
                            const std::wstring* pin = nullptr) {
  // Only the TPM takes the PIN as the key's usage secret.
  Create(provider, use, store == Store::kTpm ? pin : nullptr);
  Handle key;
  if (!OpenKey(provider, use, key)) {
    throw KeyError("key_generation_failed", "The new key cannot be opened.");
  }
  PublicKey described = Describe(store, provider, key);
  if (store == Store::kTpm && !described.hardware) {
    throw KeyError("insecure_key", "The provider did not report the key as held by hardware.");
  }
  DWORD exportPolicy = 0;
  if (store == Store::kSoftware &&
      (!DwordProperty(key, NCRYPT_EXPORT_POLICY_PROPERTY, &exportPolicy) || exportPolicy != 0)) {
    throw KeyError("insecure_key", "The software key can be exported.");
  }
  return described;
}

}  // namespace

Existing Exists() {
  std::lock_guard<std::mutex> lock(g_keys);
  Existing existing;
  Found approval;
  existing.approval = Find(Use::kApproval, approval);
  Found presence;
  existing.presence = Find(Use::kPresence, presence);
  Found lock_key;
  existing.lock = Find(Use::kLock, lock_key);
  return existing;
}

namespace {

// Deletes any old keys, makes the new ones with `make(store, provider)`, and removes whatever was made again when anything fails: a whole set or none. The
// lock is held throughout, so nothing else touches the keys in between. The store is the TPM when it can be used, and the software store otherwise.
template <typename Make>
auto FreshKeys(Make make) {
  std::lock_guard<std::mutex> lock(g_keys);
  DeleteEverywhere();
  const Store store = TpmAvailable() ? Store::kTpm : Store::kSoftware;
  const Handle provider = OpenStore(store);
  if (!provider.open()) {
    throw KeyError("key_generation_failed", "The key store of Windows cannot be opened.");
  }
  try {
    return make(store, provider);
  } catch (...) {
    // A failure to remove cannot be reported better than the first error.
    try {
      DeleteEverywhere();
    } catch (...) {
    }
    throw;
  }
}

// The lock key of a PIN enrollment, and in the software store the hash of the PIN that stands in for the TPM's check.
void CreateLock(Store store, const Handle& provider, const std::wstring& pin) {
  CreateAndDescribe(store, provider, Use::kLock, &pin);
  if (store == Store::kSoftware) KeepPin(pin);
}

}  // namespace

KeyPair CreateFreshPair(const std::wstring* pin) {
  return FreshKeys([pin](Store store, const Handle& provider) {
    KeyPair pair;
    pair.approval = CreateAndDescribe(store, provider, Use::kApproval, pin);
    pair.presence = CreateAndDescribe(store, provider, Use::kPresence);
    if (pin != nullptr) CreateLock(store, provider, *pin);
    return pair;
  });
}

PublicKey CreateFreshPresence(const std::wstring* pin) {
  return FreshKeys([pin](Store store, const Handle& provider) {
    PublicKey presence = CreateAndDescribe(store, provider, Use::kPresence);
    if (pin != nullptr) CreateLock(store, provider, *pin);
    return presence;
  });
}

PinCheck CheckPin(const std::wstring& pin) {
  std::lock_guard<std::mutex> lock(g_keys);
  Found found;
  if (!Find(Use::kLock, found)) {
    throw KeyError("key_missing", "The lock key is not there.");
  }
  if (found.store == Store::kSoftware) return CheckKeptPin(pin);
  // A fresh random value each time: the signature is thrown away, it only shows that the TPM took the PIN.
  uint8_t digest[32];
  if (BCryptGenRandom(nullptr, digest, sizeof(digest), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
    return PinCheck::kRefused;
  }
  std::vector<uint8_t> signature;
  const SECURITY_STATUS status = SignDigest(found.key, digest, &pin, &signature);
  if (status == ERROR_SUCCESS) return PinCheck::kAccepted;
  return IsLockout(status) ? PinCheck::kLocked : PinCheck::kRefused;
}

std::vector<uint8_t> Sign(Use use, const std::vector<uint8_t>& data, const std::wstring* pin) {
  if (data.size() > kMaxSignedBytes) {
    throw KeyError("sign_failed", "Too much data to sign.");
  }
  std::lock_guard<std::mutex> lock(g_keys);
  Found found;
  if (!Find(use, found)) {
    throw KeyError("key_missing", "The key is not there.");
  }
  // A key of the software store has no usage secret (the caller checked the PIN).
  if (found.store == Store::kSoftware) pin = nullptr;
  uint8_t digest[32];
  const NTSTATUS hashed = BCryptHash(
      BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
      const_cast<PUCHAR>(data.empty() ? reinterpret_cast<const uint8_t*>("") : data.data()),
      static_cast<ULONG>(data.size()), digest, sizeof(digest));
  if (hashed != 0) throw KeyError("sign_failed", "The data cannot be hashed.");
  std::vector<uint8_t> signature;
  const SECURITY_STATUS status = SignDigest(found.key, digest, pin, &signature);
  if (status != ERROR_SUCCESS) {
    if (IsLockout(status)) {
      throw KeyError("locked_out", "The TPM is locked after too many wrong tries.");
    }
    // With a PIN, a refusal is read as "not the PIN" (the TPM does not always say why).
    throw KeyError(pin != nullptr ? "auth_failed" : "sign_failed",
                   "The key did not sign (status " +
                       std::to_string(static_cast<long>(status)) + ").");
  }
  return signature;
}

void DeleteAll() {
  std::lock_guard<std::mutex> lock(g_keys);
  DeleteEverywhere();
}

}  // namespace keystore
