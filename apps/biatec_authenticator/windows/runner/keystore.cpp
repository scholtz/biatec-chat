#include "keystore.h"

#include <windows.h>
#include <bcrypt.h>
#include <ncrypt.h>

#include <cstring>

namespace keystore {
namespace {

// Names of the two keys in the provider (per Windows user). The version leaves room to replace them.
constexpr wchar_t kApprovalName[] = L"io.biatec.authenticator.approval.v1";
constexpr wchar_t kPresenceName[] = L"io.biatec.authenticator.presence.v1";

// NTE_BAD_KEYSET and NTE_NO_KEY: the key is not there.
constexpr SECURITY_STATUS kBadKeyset = static_cast<SECURITY_STATUS>(0x80090016);
constexpr SECURITY_STATUS kNoKey = static_cast<SECURITY_STATUS>(0x8009000D);

constexpr size_t kMaxSignedBytes = 1 << 20;

const wchar_t* NameOf(Use use) {
  return use == Use::kApproval ? kApprovalName : kPresenceName;
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
  if (status == kBadKeyset || status == kNoKey) return false;
  throw KeyError("key_failed", "The key cannot be opened (status " +
                                   std::to_string(static_cast<long>(status)) + ").");
}

bool DwordProperty(const Handle& key, LPCWSTR name, DWORD* value) {
  DWORD size = 0;
  return NCryptGetProperty(key.get(), name, reinterpret_cast<PBYTE>(value),
                           sizeof(DWORD), &size, 0) == ERROR_SUCCESS &&
         size == sizeof(DWORD);
}

// True when the provider says the key is in hardware and, if it says anything about export, that it cannot be exported.
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

void Create(const Handle& provider, Use use) {
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
  return NCryptOpenStorageProvider(provider.put(), MS_PLATFORM_CRYPTO_PROVIDER,
                                   0) == ERROR_SUCCESS;
}

std::optional<PublicKey> Load(Use use) {
  Handle provider = OpenProvider();
  Handle key;
  if (!OpenKey(provider, use, key)) return std::nullopt;
  return Describe(key);
}

PublicKey GenerateIfMissing(Use use) {
  Handle provider = OpenProvider();
  Handle key;
  if (!OpenKey(provider, use, key)) {
    Create(provider, use);
    if (!OpenKey(provider, use, key)) {
      throw KeyError("key_generation_failed", "The new key cannot be opened.");
    }
  }
  return Describe(key);
}

std::vector<uint8_t> Sign(Use use, const std::vector<uint8_t>& data) {
  if (data.size() > kMaxSignedBytes) {
    throw KeyError("sign_failed", "Too much data to sign.");
  }
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
  std::vector<uint8_t> signature(64);
  DWORD written = 0;
  const SECURITY_STATUS status =
      NCryptSignHash(key.get(), nullptr, digest, sizeof(digest), signature.data(),
                     static_cast<DWORD>(signature.size()), &written, 0);
  // ECDSA P-256 in CNG answers the IEEE P1363 form already: 32 bytes of r, then 32 bytes of s.
  if (status != ERROR_SUCCESS || written != 64) {
    throw KeyError("sign_failed", "The TPM did not sign (status " +
                                      std::to_string(static_cast<long>(status)) + ").");
  }
  return signature;
}

void DeleteAll() {
  Handle provider;
  if (NCryptOpenStorageProvider(provider.put(), MS_PLATFORM_CRYPTO_PROVIDER, 0) !=
      ERROR_SUCCESS) {
    return;  // no TPM: no keys of ours
  }
  for (const Use use : {Use::kApproval, Use::kPresence}) {
    Handle key;
    if (NCryptOpenKey(provider.get(), key.put(), NameOf(use), 0,
                      NCRYPT_SILENT_FLAG) == ERROR_SUCCESS) {
      // Frees the handle itself, so it is given away first.
      NCryptDeleteKey(key.release(), 0);
    }
  }
}

}  // namespace keystore
