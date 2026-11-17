// T-250: the device keys of a Windows build, held by the computer's TPM, or, on a computer whose TPM cannot be used, by the Windows account.
//
// Two P-256 keys (the approval key and the presence key) are made in the Microsoft Platform Crypto Provider, the CNG provider that keeps keys in the TPM. A key
// made there cannot be exported: this file has no call that returns private key material, only calls that sign. The keys belong to the signed-in Windows user.
//
// A computer without a TPM that can hold the keys (none, switched off in the firmware, or one that cannot make a P-256 key) gets the same keys in the Microsoft
// Software Key Storage Provider instead. They are marked as not exportable and Windows encrypts them for the Windows account, but they are software: a program
// that runs as that user, or someone who can sign in as that user, can use them. The level says so (`Store::kSoftware`), and the app warns the user.
//
// This code only keeps and uses the keys. The Windows Hello check that stands in front of the approval key is in hello.h and is joined to the signature in
// key_channels.cpp, in the one native call, so the Dart side has no way to sign with the approval key without it.
//
// On a computer where Windows Hello cannot be used, the user's check is the app's own PIN (asked by the app's screen, lib/security/pin_prompt.dart).
//  * With the TPM, the PIN is the usage secret of a third key, the lock key, and of the approval key: the TPM refuses to sign with them without it and locks
//    itself after too many wrong tries. The app keeps no copy and no hash of the PIN; "is this the PIN" is answered by asking the TPM for a signature.
//  * Without the TPM, nothing can hold the PIN that way. A salted, slow hash of it (PBKDF2) and the count of wrong tries are kept in a file that Windows
//    encrypts for the Windows account, and this code checks the PIN against it and makes the user wait after wrong tries. That stops a person at the keyboard;
//    it does not stop a program that runs as the user.
#pragma once

#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace keystore {

// Every call below is serialized: one operation on the keys at a time, whatever threads the channel calls come from.

// An error the Dart side understands: `code` is the code of the platform exception (`insecure_key`, `key_generation_failed`, `key_missing`, `sign_failed`,
// ...), `what()` is for the log, never for the user.
class KeyError : public std::runtime_error {
 public:
  KeyError(std::string code, const std::string& message)
      : std::runtime_error(message), code_(std::move(code)) {}
  const std::string& code() const { return code_; }

 private:
  std::string code_;
};

enum class Use { kApproval, kPresence, kLock };

// Where a key is kept.
enum class Store {
  kTpm,       // the Platform Crypto Provider: the TPM
  kSoftware,  // the Software Key Storage Provider: a file Windows encrypts for the Windows account
};

// The public half of a key, as the channel carries it: base64url P-256 coordinates, and where the private key lives.
struct PublicKey {
  std::string x;
  std::string y;
  Store store = Store::kSoftware;
  // True only for a key of the TPM that the provider reports as held by hardware and not exportable.
  bool hardware = false;
};

// Whether this computer has a TPM that can hold the keys: the Platform Crypto Provider opens, says it is hardware, and makes a P-256 key.
bool TpmAvailable();

// The existing key, or nothing when there is none. Never creates and never prompts.
std::optional<PublicKey> Load(Use use);

// Which keys are there, read in one step and without exporting anything.
struct Existing {
  bool approval = false;
  bool presence = false;
  // The lock key is there: this enrollment uses the app's PIN, not Windows Hello.
  bool lock = false;
};
Existing Exists();

struct KeyPair {
  PublicKey approval;
  PublicKey presence;
};

// New keys for an enrollment: any old keys are deleted first, in both stores (a key outlives the app, and an old pair must not reach the server as a new
// device), then both keys are made, in the TPM when it can be used and in the software store otherwise, and both are removed again when anything fails, so
// there is a whole pair or none. Throws `key_failed` when an old key cannot be deleted (it is never reused), `key_generation_failed` when the store refuses and
// `insecure_key` when a key of the TPM is not reported as hardware.
//
// With `pin` (a computer without Windows Hello), the lock key is made as well. In the TPM the PIN is the usage secret of the lock key and of the approval key;
// in the software store its hash is kept for the check (see the top of this file). Without `pin` no lock key and no hash is left behind.
KeyPair CreateFreshPair(const std::wstring* pin = nullptr);

// The same for single-key mode: only the presence key (and, with `pin`, the lock key).
PublicKey CreateFreshPresence(const std::wstring* pin = nullptr);

// What the key store said to a PIN.
enum class PinCheck {
  kAccepted,
  kRefused,  // not the PIN, or the check could not be made: never an acceptance
  kLocked,   // too many wrong tries: refused for a while
};

// Checks `pin`: a signature of the TPM with the lock key, or, in the software store, the kept hash. Throws `key_missing` when there is no lock key.
PinCheck CheckPin(const std::wstring& pin);

// ES256 over `data`: SHA-256, then ECDSA P-256, answered as the 64-byte IEEE P1363 `r || s`. Throws `key_missing` when the key is not there.
// Never asks the user for anything (the approval key's check is not here). `pin` is the usage secret of a TPM key that was made with one; a PIN the TPM
// refuses throws `auth_failed`, or `locked_out` when the TPM is locked. A key of the software store has no usage secret: the caller checks the PIN first.
std::vector<uint8_t> Sign(Use use, const std::vector<uint8_t>& data,
                          const std::wstring* pin = nullptr);

// Removes every key, in both stores, and the kept hash of the PIN. A key that is not there is not an error; one that cannot be deleted is (`key_failed`).
void DeleteAll();

// base64url without padding (RFC 4648, section 5).
std::string Base64Url(const uint8_t* data, size_t size);

}  // namespace keystore
