#include "keystore.h"

#include <windows.h>
#include <bcrypt.h>
#include <ncrypt.h>

#include <cstring>
#include <cwchar>
#include <mutex>

namespace keystore {
namespace {

// Names of the two keys in the provider (per Windows user). The version leaves room to replace them.
constexpr wchar_t kApprovalName[] = L"io.biatec.authenticator.approval.v1";
constexpr wchar_t kPresenceName[] = L"io.biatec.authenticator.presence.v1";
// The key that carries the app's PIN (only on a computer without Windows Hello). It signs nothing that leaves the computer: a signature with it says the PIN is right.
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
  ~Handle() {
    if (h_ != 0) NCryptFreeObject(h_);
  }
  ULONG_PTR* put() { return &h_; }
  ULONG_PTR get() const { return h_; }
  // Gives the handle away (NCryptDeleteKey frees it itself).
  ULONG_PTR release() {
    ULONG_PTR h = h_;
    h_ = 0;
    return h;
  }

 private:
  ULONG_PTR h_ = 0;
};

Handle OpenProvider() {
  Handle provider;
  const SECURITY_STATUS status =
      NCryptOpenStorageProvider(provider.put(), MS_PLATFORM_CRYPTO_PROVIDER, 0);
  if (status != ERROR_SUCCESS) {
    throw KeyError("no_tpm", "The Platform Crypto Provider cannot be opened (status " +
                                 std::to_string(static_cast<long>(status)) + ").");
  }
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

bool DwordProperty(const Handle& key, LPCWSTR name, DWORD* value) {
  DWORD size = 0;
  return NCryptGetProperty(key.get(), name, reinterpret_cast<PBYTE>(value),
                           sizeof(DWORD), &size, 0) == ERROR_SUCCESS &&
         size == sizeof(DWORD);
}

// True when the provider says the key is in hardware and, if it says anything about export, that it cannot be exported. The hardware flag must be there; the
// export policy is read as well, and a provider that does not answer that query is not held against the key (every key of the Platform Crypto Provider is made
// inside the TPM and cannot leave it; refusing here would refuse every enrollment on a driver that does not answer the query). Step 4 of the manual checklist looks
// at the real keys.
bool InHardware(const Handle& key) {
  DWORD impl = 0;
  if (!DwordProperty(key, NCRYPT_IMPL_TYPE_PROPERTY, &impl)) return false;
  if ((impl & NCRYPT_IMPL_HARDWARE_FLAG) == 0) return false;
  DWORD exportPolicy = 0;
  if (DwordProperty(key, NCRYPT_EXPORT_POLICY_PROPERTY, &exportPolicy) &&
      exportPolicy != 0) {
    return false;
  }
  return true;
}

PublicKey Describe(const Handle& key) {
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
  key_out.hardware = InHardware(key);
  return key_out;
}

// Gives the key handle the PIN: before the key is finalized it becomes the key's usage secret, on an open key it is what the TPM checks at the signature.
SECURITY_STATUS SetPin(const Handle& key, const std::wstring& pin) {
  return NCryptSetProperty(
      key.get(), NCRYPT_PIN_PROPERTY,
      reinterpret_cast<PBYTE>(const_cast<wchar_t*>(pin.c_str())),
      static_cast<DWORD>((pin.size() + 1) * sizeof(wchar_t)), 0);
}

// ECDSA P-256 in the TPM over a SHA-256 `digest`. Returns the status of the signature; `signature` is the 64-byte `r || s` on success.
SECURITY_STATUS SignDigest(const Handle& key, const uint8_t* digest, const std::wstring* pin,
                           std::vector<uint8_t>* signature) {
  if (pin != nullptr) {
    const SECURITY_STATUS set = SetPin(key, *pin);
    if (set != ERROR_SUCCESS) return set;
  }
  signature->assign(64, 0);
  DWORD written = 0;
  // With a PIN the call is silent: a refused PIN is an answer, never a dialog of the provider.
  const SECURITY_STATUS status = NCryptSignHash(
      key.get(), nullptr, const_cast<PBYTE>(digest), 32, signature->data(),
      static_cast<DWORD>(signature->size()), &written, pin != nullptr ? NCRYPT_SILENT_FLAG : 0);
  // ECDSA P-256 in CNG answers the IEEE P1363 form already: 32 bytes of r, then 32 bytes of s.
  if (status == ERROR_SUCCESS && written != 64) return NTE_FAIL;
  return status;
}

void Create(const Handle& provider, Use use, const std::wstring* pin) {
  Handle key;
  SECURITY_STATUS status = NCryptCreatePersistedKey(
      provider.get(), key.put(), BCRYPT_ECDSA_P256_ALGORITHM, NameOf(use), 0, 0);
  if (status != ERROR_SUCCESS) {
    throw KeyError("key_generation_failed",
                   "The key cannot be created (status " +
                       std::to_string(static_cast<long>(status)) + ").");
  }
  // For signing only. No export policy is set: a key of this provider cannot leave the TPM.
  DWORD usage = NCRYPT_ALLOW_SIGNING_FLAG;
  status = NCryptSetProperty(key.get(), NCRYPT_KEY_USAGE_PROPERTY,
                             reinterpret_cast<PBYTE>(&usage), sizeof(usage), 0);
  if (status == ERROR_SUCCESS && pin != nullptr) status = SetPin(key, *pin);
  if (status == ERROR_SUCCESS) {
    status = NCryptFinalizeKey(key.get(), NCRYPT_SILENT_FLAG);
  }
  if (status != ERROR_SUCCESS) {
    throw KeyError("key_generation_failed",
                   "The key cannot be finalized (status " +
                       std::to_string(static_cast<long>(status)) + ").");
  }
}

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
  Handle provider;
  if (NCryptOpenStorageProvider(provider.put(), MS_PLATFORM_CRYPTO_PROVIDER, 0) !=
      ERROR_SUCCESS) {
    return false;
  }
  // The provider must also offer ECDSA P-256 signatures (a TPM 1.2, or a restricted firmware TPM, opens the provider and cannot make the key). When the provider
  // cannot be asked, the key creation says so later.
  DWORD count = 0;
  NCryptAlgorithmName* algorithms = nullptr;
  if (NCryptEnumAlgorithms(provider.get(), NCRYPT_SIGNATURE_OPERATION, &count,
                           &algorithms, 0) != ERROR_SUCCESS ||
      algorithms == nullptr) {
    return true;
  }
  bool p256 = false;
  for (DWORD i = 0; i < count; i++) {
    // P-256 by its CNG name, or the generic ECDSA name a provider may use for its curves. A provider that offers only other curves, or none (a TPM 1.2), is refused.
    if (algorithms[i].pszName != nullptr &&
        (wcscmp(algorithms[i].pszName, BCRYPT_ECDSA_P256_ALGORITHM) == 0 ||
         wcscmp(algorithms[i].pszName, L"ECDSA") == 0)) {
      p256 = true;
    }
  }
  NCryptFreeBuffer(algorithms);
  return p256;
}

std::optional<PublicKey> Load(Use use) {
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider = OpenProvider();
  Handle key;
  if (!OpenKey(provider, use, key)) return std::nullopt;
  return Describe(key);
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

void DeleteBoth(const Handle& provider) {
  // Every key is tried, and the first failure is reported after.
  std::optional<KeyError> failure;
  for (const Use use : {Use::kApproval, Use::kPresence, Use::kLock}) {
    try {
      DeleteKey(provider, use);
    } catch (const KeyError& e) {
      if (!failure) failure = e;
    }
  }
  if (failure) throw *failure;
}

// Makes the key and reads it back. A key that is not reported as hardware is removed and refused.
PublicKey CreateAndDescribe(const Handle& provider, Use use, const std::wstring* pin = nullptr) {
  Create(provider, use, pin);
  Handle key;
  if (!OpenKey(provider, use, key)) {
    throw KeyError("key_generation_failed", "The new key cannot be opened.");
  }
  PublicKey described = Describe(key);
  if (!described.hardware) {
    throw KeyError("insecure_key", "The provider did not report the key as held by hardware.");
  }
  return described;
}

}  // namespace

Existing Exists() {
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider = OpenProvider();
  Existing existing;
  Handle approval;
  existing.approval = OpenKey(provider, Use::kApproval, approval);
  Handle presence;
  existing.presence = OpenKey(provider, Use::kPresence, presence);
  Handle lock_key;
  existing.lock = OpenKey(provider, Use::kLock, lock_key);
  return existing;
}

namespace {

// Deletes any old keys, makes the new ones with `make(provider)`, and removes whatever was made again when anything fails: a whole set or none. The lock is held
// throughout, so nothing else touches the keys in between.
template <typename Make>
auto FreshKeys(Make make) {
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider = OpenProvider();
  DeleteBoth(provider);
  try {
    return make(provider);
  } catch (...) {
    // A failure to remove cannot be reported better than the first error.
    try {
      DeleteBoth(provider);
    } catch (...) {
    }
    throw;
  }
}

}  // namespace

KeyPair CreateFreshPair(const std::wstring* pin) {
  return FreshKeys([pin](const Handle& provider) {
    KeyPair pair;
    pair.approval = CreateAndDescribe(provider, Use::kApproval, pin);
    pair.presence = CreateAndDescribe(provider, Use::kPresence);
    if (pin != nullptr) CreateAndDescribe(provider, Use::kLock, pin);
    return pair;
  });
}

PublicKey CreateFreshPresence(const std::wstring* pin) {
  return FreshKeys([pin](const Handle& provider) {
    PublicKey presence = CreateAndDescribe(provider, Use::kPresence);
    if (pin != nullptr) CreateAndDescribe(provider, Use::kLock, pin);
    return presence;
  });
}

PinCheck CheckPin(const std::wstring& pin) {
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider = OpenProvider();
  Handle key;
  if (!OpenKey(provider, Use::kLock, key)) {
    throw KeyError("key_missing", "The lock key is not there.");
  }
  // A fresh random value each time: the signature is thrown away, it only shows that the TPM took the PIN.
  uint8_t digest[32];
  if (BCryptGenRandom(nullptr, digest, sizeof(digest), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) {
    return PinCheck::kRefused;
  }
  std::vector<uint8_t> signature;
  const SECURITY_STATUS status = SignDigest(key, digest, &pin, &signature);
  if (status == ERROR_SUCCESS) return PinCheck::kAccepted;
  return IsLockout(status) ? PinCheck::kLocked : PinCheck::kRefused;
}

std::vector<uint8_t> Sign(Use use, const std::vector<uint8_t>& data, const std::wstring* pin) {
  if (data.size() > kMaxSignedBytes) {
    throw KeyError("sign_failed", "Too much data to sign.");
  }
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider = OpenProvider();
  Handle key;
  if (!OpenKey(provider, use, key)) {
    throw KeyError("key_missing", "The key is not there.");
  }
  uint8_t digest[32];
  const NTSTATUS hashed = BCryptHash(
      BCRYPT_SHA256_ALG_HANDLE, nullptr, 0,
      const_cast<PUCHAR>(data.empty() ? reinterpret_cast<const uint8_t*>("") : data.data()),
      static_cast<ULONG>(data.size()), digest, sizeof(digest));
  if (hashed != 0) throw KeyError("sign_failed", "The data cannot be hashed.");
  std::vector<uint8_t> signature;
  const SECURITY_STATUS status = SignDigest(key, digest, pin, &signature);
  if (status != ERROR_SUCCESS) {
    if (IsLockout(status)) {
      throw KeyError("locked_out", "The TPM is locked after too many wrong tries.");
    }
    // With a PIN, a refusal is read as "not the PIN" (the TPM does not always say why).
    throw KeyError(pin != nullptr ? "auth_failed" : "sign_failed",
                   "The TPM did not sign (status " +
                       std::to_string(static_cast<long>(status)) + ").");
  }
  return signature;
}

void DeleteAll() {
  std::lock_guard<std::mutex> lock(g_keys);
  Handle provider;
  if (NCryptOpenStorageProvider(provider.put(), MS_PLATFORM_CRYPTO_PROVIDER, 0) !=
      ERROR_SUCCESS) {
    return;  // no TPM: no keys of ours
  }
  DeleteBoth(provider);
}

}  // namespace keystore
