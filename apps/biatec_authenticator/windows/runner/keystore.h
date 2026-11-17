// T-250: the device keys of a Windows build, held by the computer's TPM.
//
// Two P-256 keys (the approval key and the presence key) are made in the Microsoft Platform Crypto Provider, the CNG provider that keeps keys in the TPM. A key
// made there cannot be exported: this file has no call that returns private key material, only calls that sign. The keys belong to the signed-in Windows user.
//
// This code only keeps and uses the keys. The Windows Hello check that stands in front of the approval key is in hello.h and is joined to the signature in
// key_channels.cpp, in the one native call, so the Dart side has no way to sign with the approval key without it.
#pragma once

#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace keystore {

// Every call below is serialized: one operation on the keys at a time, whatever threads the channel calls come from.

// An error the Dart side understands: `code` is the code of the platform exception (`no_tpm`, `insecure_key`, `key_generation_failed`, `key_missing`,
// `sign_failed`, ...), `what()` is for the log, never for the user.
class KeyError : public std::runtime_error {
 public:
  KeyError(std::string code, const std::string& message)
      : std::runtime_error(message), code_(std::move(code)) {}
  const std::string& code() const { return code_; }

 private:
  std::string code_;
};

enum class Use { kApproval, kPresence };

// The public half of a key, as the channel carries it: base64url P-256 coordinates, and where the provider says the private key lives.
struct PublicKey {
  std::string x;
  std::string y;
  // True only when the provider reports the key as held by hardware (the TPM) and not exportable. Anything else is reported as software, which the app refuses.
  bool hardware = false;
};

// Whether the Platform Crypto Provider can be opened, that is whether this computer has a TPM that CNG can use.
bool TpmAvailable();

// The existing key, or nothing when there is none. Never creates and never prompts.
std::optional<PublicKey> Load(Use use);

// Which keys are there, read in one step and without exporting anything.
struct Existing {
  bool approval = false;
  bool presence = false;
};
Existing Exists();

struct KeyPair {
  PublicKey approval;
  PublicKey presence;
};

// New keys for an enrollment: any old pair is deleted first (a TPM key outlives the app, and an old pair must not reach the server as a new device), then both
// keys are made, and both are removed again when anything fails, so there is a whole pair or none. Throws `no_tpm`, `key_failed` when an old key cannot be
// deleted (it is never reused), `key_generation_failed` when the TPM refuses and `insecure_key` when a key is not reported as hardware.
KeyPair CreateFreshPair();

// The same for single-key mode: only the presence key.
PublicKey CreateFreshPresence();

// ES256 over `data`: SHA-256, then ECDSA P-256 in the TPM, answered as the 64-byte IEEE P1363 `r || s`. Throws `key_missing` when the key is not there.
// Never asks the user for anything (the approval key's check is not here).
std::vector<uint8_t> Sign(Use use, const std::vector<uint8_t>& data);

// Removes both keys. A key that is not there is not an error; a key that is there and cannot be deleted is (`key_failed`).
void DeleteAll();

// base64url without padding (RFC 4648, section 5).
std::string Base64Url(const uint8_t* data, size_t size);

}  // namespace keystore
