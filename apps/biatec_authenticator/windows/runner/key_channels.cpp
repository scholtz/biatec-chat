#include "key_channels.h"

#include <flutter/standard_method_codec.h>

#include <atomic>
#include <optional>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "hello.h"
#include "keystore.h"

namespace {

using flutter::EncodableMap;
using flutter::EncodableValue;
using flutter::MethodCall;
using flutter::MethodResult;
using Result = std::unique_ptr<MethodResult<EncodableValue>>;

// One Windows Hello prompt at a time, for the approvals and the app lock together.
std::atomic<bool> g_prompt_open{false};

class PromptGuard {
 public:
  PromptGuard() {
    bool expected = false;
    owns_ = g_prompt_open.compare_exchange_strong(expected, true);
  }
  ~PromptGuard() {
    if (owns_) g_prompt_open = false;
  }
  PromptGuard(const PromptGuard&) = delete;
  PromptGuard& operator=(const PromptGuard&) = delete;
  bool owns() const { return owns_; }

 private:
  bool owns_ = false;
};

std::wstring Widen(const std::string& utf8) {
  if (utf8.empty()) return L"";
  const int size = MultiByteToWideChar(CP_UTF8, 0, utf8.data(),
                                       static_cast<int>(utf8.size()), nullptr, 0);
  if (size <= 0) return L"";
  std::wstring out(static_cast<size_t>(size), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()),
                      out.data(), size);
  return out;
}

EncodableValue Text(const std::string& s) { return EncodableValue(s); }

EncodableValue KeyMap(const keystore::PublicKey& key, bool approval) {
  EncodableMap map;
  map[Text("x")] = Text(key.x);
  map[Text("y")] = Text(key.y);
  // `tpm` only when the provider says hardware and not exportable. `windows_account` is a key of the software store of a computer whose TPM cannot be used:
  // the app enrolls it and warns the user. Anything else is reported as software, which the app refuses.
  map[Text("securityLevel")] = Text(key.hardware ? "tpm" : key.store == keystore::Store::kSoftware ? "windows_account" : "software");
  // Not `authBound`: that says the hardware itself refuses the key without the biometric gesture, which is true on a phone and not here. The approval key is used
  // only behind a Windows Hello check made in the same native call (the app's gate, docs/authenticator-windows-key-store.md), or with the app's PIN, and
  // `appGated` covers both without claiming more than the weaker one gives.
  map[Text("authBound")] = EncodableValue(false);
  map[Text("appGated")] = EncodableValue(approval);
  return EncodableValue(map);
}

const EncodableMap* ArgsOf(const MethodCall<EncodableValue>& call) {
  return std::get_if<EncodableMap>(call.arguments());
}

const std::string* StringArg(const EncodableMap* args, const char* name) {
  if (args == nullptr) return nullptr;
  const auto it = args->find(Text(name));
  return it == args->end() ? nullptr : std::get_if<std::string>(&it->second);
}

const std::vector<uint8_t>* BytesArg(const EncodableMap* args, const char* name) {
  if (args == nullptr) return nullptr;
  const auto it = args->find(Text(name));
  return it == args->end() ? nullptr : std::get_if<std::vector<uint8_t>>(&it->second);
}

std::optional<std::string> OptionalString(const EncodableMap* args, const char* name) {
  const std::string* value = StringArg(args, name);
  return value == nullptr ? std::nullopt : std::optional<std::string>(*value);
}

std::optional<keystore::Use> UseOf(const EncodableMap* args) {
  const std::string* use = StringArg(args, "use");
  if (use == nullptr) return std::nullopt;
  if (*use == "approval") return keystore::Use::kApproval;
  if (*use == "presence") return keystore::Use::kPresence;
  return std::nullopt;
}

// The PIN of the app (a computer without Windows Hello). The app's own screen asks for it (lib/security/pin_prompt.dart) and sends it with the call. It is not
// kept: this copy is wiped when the call ends, and the key store is what checks it (the TPM, or the kept hash on a computer without one: keystore.h).
class Pin {
 public:
  explicit Pin(const std::string& utf8) : value_(Widen(utf8)) {}
  // Copies and wipes the other: a moved-from string can keep its characters in its small buffer.
  Pin(Pin&& other) : value_(other.value_) { other.Wipe(); }
  Pin(const Pin&) = delete;
  Pin& operator=(const Pin&) = delete;
  ~Pin() { Wipe(); }
  const std::wstring& value() const { return value_; }

 private:
  void Wipe() {
    if (!value_.empty()) SecureZeroMemory(value_.data(), value_.size() * sizeof(wchar_t));
    value_.clear();
  }
  std::wstring value_;
};

// The same limits as the app's screen (lib/security/pin_prompt.dart), held here as well: the Dart side is not what decides.
constexpr size_t kMinPinLength = 6;
constexpr size_t kMaxPinLength = 64;

// The user's check of a new enrollment. Windows Hello when it can be used: then there is no PIN, and one that was sent is not used. Otherwise (not set up, no
// device, switched off by a policy, a remote session) the keys are made with a PIN of the app (keystore.h). Without one the answer is `pin_required`: the app
// asks the user for a new PIN and calls again. Nothing is made before that.
std::optional<Pin> NewPinUnlessHello(const std::string* sent) {
  // "Cannot be used right now" may pass (busy, a slow start): it is asked again before the choice is made, because the choice holds for the whole enrollment.
  hello::Availability availability = hello::Check();
  for (int again = 0; again < 2 && availability == hello::Availability::kUnavailable; again++) {
    Sleep(1000);
    availability = hello::Check();
  }
  if (availability == hello::Availability::kAvailable) return std::nullopt;
  if (sent == nullptr) {
    throw keystore::KeyError("pin_required", "Windows Hello cannot be used: a PIN of the app is needed.");
  }
  Pin pin(*sent);
  if (pin.value().size() < kMinPinLength || pin.value().size() > kMaxPinLength) {
    throw keystore::KeyError("bad_pin", "The PIN needs 6 to 64 characters.");
  }
  return pin;
}

// Whether this enrollment uses the app's PIN (its lock key is there) and not Windows Hello. Throws when the key store cannot be read: that is neither answer.
bool PinMode() { return keystore::Exists().lock; }

// Both keys, or neither: a half set (an interrupted enrollment) is removed and made again.
EncodableValue GenerateKeys(const std::string* sent_pin) {
  // No TPM is not a refusal: the keys are then made in the software store of Windows, and their level says so (keystore.h).
  const std::optional<Pin> pin = NewPinUnlessHello(sent_pin);
  // New keys for every enrollment, a whole pair or none (see keystore.h): a key outlives the app, so an old pair is never handed to a new enrollment.
  const keystore::KeyPair pair = keystore::CreateFreshPair(pin ? &pin->value() : nullptr);
  EncodableMap map;
  map[Text("approval")] = KeyMap(pair.approval, true);
  map[Text("presence")] = KeyMap(pair.presence, false);
  return EncodableValue(map);
}

EncodableValue GeneratePresence(const std::string* sent_pin) {
  // Single-key mode: the approvals are gated by the app lock, so the app lock needs its check too (Windows Hello, or the PIN chosen for this enrollment).
  const std::optional<Pin> pin = NewPinUnlessHello(sent_pin);
  // New keys for every enrollment (see GenerateKeys).
  return KeyMap(keystore::CreateFreshPresence(pin ? &pin->value() : nullptr), false);
}

EncodableValue LoadKey(keystore::Use use) {
  const auto key = keystore::Load(use);
  if (!key) return EncodableValue();
  return KeyMap(*key, use == keystore::Use::kApproval);
}

EncodableValue KeysState() {
  // A provider that cannot be opened is thrown, not read as "no keys": the keys may be fine and the TPM busy, and Dart keeps the state it had.
  const keystore::Existing existing = keystore::Exists();  // both read in one step
  if (existing.approval && existing.presence) return Text("ok");
  // Not "approval_key_invalidated": no change of the biometrics invalidates a TPM key, and a presence key alone is the normal state of the single-key mode
  // (which a phone also reports as "missing").
  return Text("missing");
}

EncodableValue Probe() {
  EncodableMap map;
  // Whether the TPM can hold the keys. A computer without Windows Hello enrolls with the app's PIN, so Windows Hello is not asked here.
  map[Text("tpm")] = EncodableValue(keystore::TpmAvailable());
  // The software store of Windows is always there: a computer without a usable TPM enrolls with it, and the app warns the user.
  map[Text("software")] = EncodableValue(true);
  return EncodableValue(map);
}

// The PIN of the app, checked by the key store (the TPM: a signature with the lock key; without a TPM: the kept hash). Without one the answer is `pin_required`: the app asks the user and calls again.
// Throws unless it was accepted.
Pin RequirePin(const std::string* sent) {
  if (sent == nullptr) {
    throw keystore::KeyError("pin_required", "The PIN of the app is needed.");
  }
  Pin pin(*sent);
  switch (keystore::CheckPin(pin.value())) {
    case keystore::PinCheck::kAccepted:
      break;
    case keystore::PinCheck::kLocked:
      throw keystore::KeyError("locked_out", "Too many wrong tries. The PIN is refused for a while. Try again later.");
    default:
      throw keystore::KeyError("auth_failed", "The PIN was not accepted.");
  }
  return pin;
}

// An approval on a computer that uses the app's PIN. The PIN is checked against the lock key first, so it is the PIN of this enrollment whatever the approval
// key was made with; the approval key then signs with it.
EncodableValue SignApprovalWithPin(const std::vector<uint8_t>& data, const std::string* sent_pin) {
  const Pin pin = RequirePin(sent_pin);
  return EncodableValue(keystore::Sign(keystore::Use::kApproval, data, &pin.value()));
}

// The user's check and the signature are one operation: the key signs only after Windows Hello said verified, or, on a computer without it, with the app's PIN,
// which the key store checks (the TPM itself, where there is one).
EncodableValue SignApproval(HWND window, const std::vector<uint8_t>& data,
                            const std::string& title, const std::string& subtitle,
                            const std::string* sent_pin) {
  const keystore::Existing existing = keystore::Exists();
  if (!existing.approval) {
    // Not "invalidated": no change of the biometrics invalidates a TPM key (see KeysState). The key is gone, and the device enrolls again.
    throw keystore::KeyError("key_missing", "The approval key is gone.");
  }
  // A PIN enrollment never reaches the Windows Hello path (and shows no native prompt: the app's screen asked for the PIN).
  if (existing.lock) return SignApprovalWithPin(data, sent_pin);
  PromptGuard guard;
  if (!guard.owns()) {
    throw keystore::KeyError("prompt_active", "Another Windows Hello prompt is open. Try again.");
  }
  std::string text = title;
  if (!subtitle.empty()) text += (text.empty() ? "" : " - ") + subtitle;
  switch (hello::Verify(window, Widen(text))) {
    case hello::Outcome::kVerified:
      break;
    case hello::Outcome::kCanceled:
      throw keystore::KeyError("cancelled", "The Windows Hello check was cancelled.");
    case hello::Outcome::kRetriesExhausted:
      throw keystore::KeyError("locked_out", "Windows Hello is locked after too many tries. Try again later.");
    case hello::Outcome::kNotConfigured:
    case hello::Outcome::kNoDevice:
      throw keystore::KeyError("no_biometrics", "Windows Hello is not set up on this computer.");
    case hello::Outcome::kBusy:
      throw keystore::KeyError("prompt_active", "Another Windows Hello prompt is still open. Try again.");
    default:
      throw keystore::KeyError("auth_failed", "The Windows Hello check did not succeed.");
  }
  return EncodableValue(keystore::Sign(keystore::Use::kApproval, data));
}

// The app lock: the strings the Dart side reads (lib/security/biometric_policy.dart).
EncodableValue BiometricStatus() {
  try {
    // The app's PIN is always there to be asked.
    if (PinMode()) return Text("available");
  } catch (const keystore::KeyError&) {
    // The key store cannot be read now: not "no Windows Hello", only "not now".
    return Text("unavailable");
  }
  switch (hello::Check()) {
    case hello::Availability::kAvailable:
      return Text("available");
    case hello::Availability::kNotConfigured:
      return Text("none_enrolled");
    case hello::Availability::kNoDevice:
      return Text("no_hardware");
    default:
      return Text("unavailable");
  }
}

EncodableValue BiometricAuthenticate(HWND window, const std::string& reason,
                                     const std::string* sent_pin) {
  bool pin_mode = false;
  try {
    pin_mode = PinMode();
  } catch (const keystore::KeyError&) {
    return Text("unavailable");
  }
  if (pin_mode) {
    // No PIN came with the call: the app asks the user on its own screen and calls again.
    if (sent_pin == nullptr) return Text("pin_required");
    try {
      RequirePin(sent_pin);
      return Text("success");
    } catch (const keystore::KeyError& e) {
      if (e.code() == "locked_out") return Text("lockout");
      // A refused PIN, a lock key that is gone, a TPM that cannot be reached: nothing opens.
      return Text("failed");
    }
  }
  PromptGuard guard;
  // Another Windows Hello prompt is open (an approval's): not a wrong finger, so not "failed"; the lock reads "unavailable" as "cannot be used now".
  if (!guard.owns()) return Text("unavailable");
  switch (hello::Verify(window, Widen(reason))) {
    case hello::Outcome::kVerified:
      return Text("success");
    case hello::Outcome::kCanceled:
      return Text("cancelled");
    case hello::Outcome::kRetriesExhausted:
      return Text("lockout");
    case hello::Outcome::kNotConfigured:
      return Text("none_enrolled");
    case hello::Outcome::kNoDevice:
    case hello::Outcome::kBusy:
      return Text("unavailable");
    default:
      return Text("failed");
  }
}

}  // namespace

KeyChannels::KeyChannels(flutter::BinaryMessenger* messenger, HWND window)
    : shared_(std::make_shared<Shared>()) {
  shared_->window = window;

  keystore_ = std::make_unique<flutter::MethodChannel<EncodableValue>>(
      messenger, "io.biatec.authenticator/keystore",
      &flutter::StandardMethodCodec::GetInstance());
  keystore_->SetMethodCallHandler(
      [this, window](const MethodCall<EncodableValue>& call, Result result) {
        const std::string& method = call.method_name();
        const EncodableMap* args = ArgsOf(call);
        if (method == "generateKeys") {
          RunAsync(std::move(result), [pin = OptionalString(args, "pin")] {
            return GenerateKeys(pin ? &*pin : nullptr);
          });
        } else if (method == "generatePresence") {
          // Single-key mode: only the key that needs no prompt; the approvals are gated by the app lock's check (Windows Hello, or the app's PIN).
          RunAsync(std::move(result), [pin = OptionalString(args, "pin")] {
            return GeneratePresence(pin ? &*pin : nullptr);
          });
        } else if (method == "load") {
          const auto use = UseOf(args);
          if (!use) {
            result->Error("bad_args", "use is required");
            return;
          }
          RunAsync(std::move(result), [use] { return LoadKey(*use); });
        } else if (method == "keysState") {
          RunAsync(std::move(result), [] { return KeysState(); });
        } else if (method == "probe") {
          RunAsync(std::move(result), [] { return Probe(); });
        } else if (method == "signPresence") {
          const auto* data = BytesArg(args, "data");
          if (data == nullptr) {
            result->Error("bad_args", "data is required");
            return;
          }
          RunAsync(std::move(result), [bytes = *data] {
            return EncodableValue(keystore::Sign(keystore::Use::kPresence, bytes));
          });
        } else if (method == "signApproval") {
          const auto* data = BytesArg(args, "data");
          if (data == nullptr) {
            result->Error("bad_args", "data is required");
            return;
          }
          const std::string* title = StringArg(args, "title");
          const std::string* subtitle = StringArg(args, "subtitle");
          RunAsync(std::move(result),
                   [window, bytes = *data, t = title ? *title : std::string(),
                    s = subtitle ? *subtitle : std::string(),
                    pin = OptionalString(args, "pin")] {
                     return SignApproval(window, bytes, t, s, pin ? &*pin : nullptr);
                   });
        } else if (method == "deleteKeys") {
          RunAsync(std::move(result), [] {
            keystore::DeleteAll();
            return EncodableValue();
          });
        } else if (method == "loadLegacy") {
          // An install from before the two keys never existed on Windows.
          result->Success(EncodableValue());
        } else if (method == "signLegacy") {
          result->Error("key_missing", "There is no legacy key on this platform.");
        } else if (method == "delete") {
          result->Success(EncodableValue());
        } else {
          result->NotImplemented();
        }
      });

  biometric_ = std::make_unique<flutter::MethodChannel<EncodableValue>>(
      messenger, "io.biatec.authenticator/biometric",
      &flutter::StandardMethodCodec::GetInstance());
  biometric_->SetMethodCallHandler(
      [this, window](const MethodCall<EncodableValue>& call, Result result) {
        const std::string& method = call.method_name();
        if (method == "status") {
          RunAsync(std::move(result), [] { return BiometricStatus(); });
        } else if (method == "authenticate") {
          const std::string* reason = StringArg(ArgsOf(call), "reason");
          RunAsync(std::move(result), [window, r = reason ? *reason : std::string(),
                                       pin = OptionalString(ArgsOf(call), "pin")] {
            return BiometricAuthenticate(window, r, pin ? &*pin : nullptr);
          });
        } else {
          result->NotImplemented();
        }
      });
}

KeyChannels::~KeyChannels() {
  {
    // Workers that finish later find no window and queue their answers for nobody.
    std::lock_guard<std::mutex> lock(shared_->mutex);
    shared_->window = nullptr;
    shared_->queue.clear();
  }
  // The unanswered replies are dropped here, on the window's thread, while the engine is still alive.
  pending_.clear();
}

void KeyChannels::RunAsync(Result result, std::function<EncodableValue()> work) {
  const uint64_t id = next_id_++;
  pending_[id] = std::move(result);
  // The worker holds only data and the shared queue, never the reply object or `this`. An error of ours is answered with its code; anything else as
  // `keystore_error`.
  try {
  std::thread([shared = shared_, id, work = std::move(work)]() {
    Done done;
    done.id = id;
    try {
      done.value = work();
    } catch (const keystore::KeyError& e) {
      done.code = e.code();
      done.message = e.what();
    } catch (...) {
      done.code = "keystore_error";
      done.message = "The key store could not do that.";
    }
    HWND window = nullptr;
    {
      std::lock_guard<std::mutex> lock(shared->mutex);
      window = shared->window;
      if (window != nullptr) shared->queue.push_back(std::move(done));
    }
    // A message that cannot be posted (the window's queue is full) would leave the call open for ever: the worker tries again for a while, and stops when the
    // window is gone.
    if (window != nullptr) {
      for (int attempt = 0; attempt < 100 && !PostMessage(window, kRunQueuedMessage, 0, 0); attempt++) {
        if (!IsWindow(window)) break;
        Sleep(50);
      }
    }
  }).detach();
  } catch (const std::exception&) {
    // No thread could be made: the call is answered now, never left open.
    Result failed = std::move(pending_[id]);
    pending_.erase(id);
    failed->Error("keystore_error", "The key store could not do that.");
  }
}

void KeyChannels::RunQueued() {
  std::deque<Done> done;
  {
    std::lock_guard<std::mutex> lock(shared_->mutex);
    done.swap(shared_->queue);
  }
  for (auto& d : done) {
    const auto it = pending_.find(d.id);
    if (it == pending_.end()) continue;
    Result result = std::move(it->second);
    pending_.erase(it);
    if (d.code.empty()) {
      result->Success(d.value);
    } else {
      result->Error(d.code, d.message);
    }
  }
}

void KeyChannels::OnSessionLocked() {
  biometric_->InvokeMethod("screenLocked", nullptr);
}
