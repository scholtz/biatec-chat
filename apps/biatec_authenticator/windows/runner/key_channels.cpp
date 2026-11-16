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

const char* HelloAvailabilityName(hello::Availability availability) {
  switch (availability) {
    case hello::Availability::kAvailable:
      return "available";
    case hello::Availability::kNotConfigured:
      return "not_configured";
    case hello::Availability::kNoDevice:
      return "no_device";
    default:
      return "unavailable";
  }
}

EncodableValue Text(const std::string& s) { return EncodableValue(s); }

EncodableValue KeyMap(const keystore::PublicKey& key, bool approval) {
  EncodableMap map;
  map[Text("x")] = Text(key.x);
  map[Text("y")] = Text(key.y);
  // `tpm` only when the provider says hardware and not exportable; anything else is reported as software, which the app refuses.
  map[Text("securityLevel")] = Text(key.hardware ? "tpm" : "software");
  // The approval key is used only behind a Windows Hello check made in the same native call. This is the app's gate, not a property the hardware enforces (the
  // document of this task says so); the flag says the gate exists.
  map[Text("authBound")] = EncodableValue(approval);
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

std::optional<keystore::Use> UseOf(const EncodableMap* args) {
  const std::string* use = StringArg(args, "use");
  if (use == nullptr) return std::nullopt;
  if (*use == "approval") return keystore::Use::kApproval;
  if (*use == "presence") return keystore::Use::kPresence;
  return std::nullopt;
}

// Both keys, or neither: a half set (an interrupted enrollment) is removed and made again. The approval key is refused unless Windows Hello can be used, so
// nothing is made that could not be used.
EncodableValue GenerateKeys() {
  if (!keystore::TpmAvailable()) {
    throw keystore::KeyError("no_tpm", "This computer has no TPM that can hold the keys.");
  }
  if (hello::Check() != hello::Availability::kAvailable) {
    throw keystore::KeyError("no_hello", "Windows Hello is not set up on this computer.");
  }
  auto approval = keystore::Load(keystore::Use::kApproval);
  auto presence = keystore::Load(keystore::Use::kPresence);
  if (!approval || !presence || !approval->hardware || !presence->hardware) {
    keystore::DeleteAll();
    approval = keystore::GenerateIfMissing(keystore::Use::kApproval);
    if (!approval->hardware) {
      keystore::DeleteAll();
      throw keystore::KeyError("insecure_key", "The TPM did not hold the approval key.");
    }
    presence = keystore::GenerateIfMissing(keystore::Use::kPresence);
  }
  EncodableMap map;
  map[Text("approval")] = KeyMap(*approval, true);
  map[Text("presence")] = KeyMap(*presence, false);
  return EncodableValue(map);
}

EncodableValue GeneratePresence() {
  return KeyMap(keystore::GenerateIfMissing(keystore::Use::kPresence), false);
}

EncodableValue LoadKey(keystore::Use use) {
  const auto key = keystore::Load(use);
  if (!key) return EncodableValue();
  return KeyMap(*key, use == keystore::Use::kApproval);
}

EncodableValue KeysState() {
  try {
    const bool approval = keystore::Load(keystore::Use::kApproval).has_value();
    const bool presence = keystore::Load(keystore::Use::kPresence).has_value();
    if (approval && presence) return Text("ok");
    // The approval key is gone while the presence key is not: it was made here and lost. Approvals stop; Deny still works.
    if (!approval && presence) return Text("approval_key_invalidated");
    return Text("missing");
  } catch (const keystore::KeyError& e) {
    if (e.code() == "no_tpm") return Text("missing");
    throw;
  }
}

EncodableValue Probe() {
  EncodableMap map;
  map[Text("tpm")] = EncodableValue(keystore::TpmAvailable());
  map[Text("hello")] = Text(HelloAvailabilityName(hello::Check()));
  return EncodableValue(map);
}

// The Windows Hello check and the signature are one operation: the key signs only after the check said verified.
EncodableValue SignApproval(HWND window, const std::vector<uint8_t>& data,
                            const std::string& title, const std::string& subtitle) {
  if (!keystore::Load(keystore::Use::kApproval)) {
    throw keystore::KeyError("approval_key_invalidated", "The approval key is gone.");
  }
  PromptGuard guard;
  if (!guard.owns()) {
    throw keystore::KeyError("prompt_active", "Another Windows Hello prompt is open. Try again.");
  }
  if (hello::Check() != hello::Availability::kAvailable) {
    throw keystore::KeyError("no_biometrics",
                             "Windows Hello is not set up on this computer. Set it up in the Windows settings, then try again.");
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
    default:
      throw keystore::KeyError("auth_failed", "The Windows Hello check did not succeed.");
  }
  return EncodableValue(keystore::Sign(keystore::Use::kApproval, data));
}

// The app lock: the strings the Dart side reads (lib/security/biometric_policy.dart).
EncodableValue BiometricStatus() {
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

EncodableValue BiometricAuthenticate(HWND window, const std::string& reason) {
  PromptGuard guard;
  if (!guard.owns()) return Text("failed");
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
      return Text("unavailable");
    default:
      return Text("failed");
  }
}

}  // namespace

KeyChannels::KeyChannels(flutter::BinaryMessenger* messenger, HWND window)
    : shared_(std::make_shared<Shared>()) {
  shared_->window = window;
  const auto shared = shared_;

  // Runs `work` on a worker thread and answers the call from the window's thread. An error of ours is answered with its code; anything else as `keystore_error`.
  const auto run_async = [shared](std::shared_ptr<Result> result,
                                  std::function<EncodableValue()> work) {
    std::thread([shared, result, work = std::move(work)]() {
      EncodableValue value;
      std::string code;
      std::string message;
      try {
        value = work();
      } catch (const keystore::KeyError& e) {
        code = e.code();
        message = e.what();
      } catch (const std::exception&) {
        code = "keystore_error";
        message = "The key store could not do that.";
      } catch (...) {
        code = "keystore_error";
        message = "The key store could not do that.";
      }
      HWND window = nullptr;
      {
        std::lock_guard<std::mutex> lock(shared->mutex);
        shared->queue.push_back([result, value, code, message]() {
          if (code.empty()) {
            (*result)->Success(value);
          } else {
            (*result)->Error(code, message);
          }
        });
        window = shared->window;
      }
      if (window != nullptr) PostMessage(window, kRunQueuedMessage, 0, 0);
    }).detach();
  };

  keystore_ = std::make_unique<flutter::MethodChannel<EncodableValue>>(
      messenger, "io.biatec.authenticator/keystore",
      &flutter::StandardMethodCodec::GetInstance());
  keystore_->SetMethodCallHandler(
      [run_async, window](const MethodCall<EncodableValue>& call, Result result) {
        const std::string& method = call.method_name();
        const EncodableMap* args = ArgsOf(call);
        auto holder = std::make_shared<Result>(std::move(result));
        if (method == "generateKeys") {
          run_async(holder, [] { return GenerateKeys(); });
        } else if (method == "generatePresence") {
          // Single-key mode: only the key that needs no prompt; the approvals are gated by the app lock's Windows Hello check.
          run_async(holder, [] { return GeneratePresence(); });
        } else if (method == "load") {
          const auto use = UseOf(args);
          if (!use) {
            (*holder)->Error("bad_args", "use is required");
            return;
          }
          run_async(holder, [use] { return LoadKey(*use); });
        } else if (method == "keysState") {
          run_async(holder, [] { return KeysState(); });
        } else if (method == "probe") {
          run_async(holder, [] { return Probe(); });
        } else if (method == "signPresence") {
          const auto* data = BytesArg(args, "data");
          if (data == nullptr) {
            (*holder)->Error("bad_args", "data is required");
            return;
          }
          run_async(holder, [bytes = *data] {
            return EncodableValue(keystore::Sign(keystore::Use::kPresence, bytes));
          });
        } else if (method == "signApproval") {
          const auto* data = BytesArg(args, "data");
          if (data == nullptr) {
            (*holder)->Error("bad_args", "data is required");
            return;
          }
          const std::string* title = StringArg(args, "title");
          const std::string* subtitle = StringArg(args, "subtitle");
          run_async(holder, [window, bytes = *data, t = title ? *title : std::string(),
                             s = subtitle ? *subtitle : std::string()] {
            return SignApproval(window, bytes, t, s);
          });
        } else if (method == "deleteKeys") {
          run_async(holder, [] {
            keystore::DeleteAll();
            return EncodableValue();
          });
        } else if (method == "loadLegacy") {
          // An install from before the two keys never existed on Windows.
          (*holder)->Success(EncodableValue());
        } else if (method == "signLegacy") {
          (*holder)->Error("key_missing", "There is no legacy key on this platform.");
        } else if (method == "delete") {
          (*holder)->Success(EncodableValue());
        } else {
          (*holder)->NotImplemented();
        }
      });

  biometric_ = std::make_unique<flutter::MethodChannel<EncodableValue>>(
      messenger, "io.biatec.authenticator/biometric",
      &flutter::StandardMethodCodec::GetInstance());
  biometric_->SetMethodCallHandler(
      [run_async, window](const MethodCall<EncodableValue>& call, Result result) {
        const std::string& method = call.method_name();
        auto holder = std::make_shared<Result>(std::move(result));
        if (method == "status") {
          run_async(holder, [] { return BiometricStatus(); });
        } else if (method == "authenticate") {
          const std::string* reason = StringArg(ArgsOf(call), "reason");
          run_async(holder, [window, r = reason ? *reason : std::string()] {
            return BiometricAuthenticate(window, r);
          });
        } else {
          (*holder)->NotImplemented();
        }
      });
}

KeyChannels::~KeyChannels() {
  // Workers that finish later find no window and queue their answers for nobody.
  std::lock_guard<std::mutex> lock(shared_->mutex);
  shared_->window = nullptr;
  shared_->queue.clear();
}

void KeyChannels::RunQueued() {
  std::deque<std::function<void()>> tasks;
  {
    std::lock_guard<std::mutex> lock(shared_->mutex);
    tasks.swap(shared_->queue);
  }
  for (auto& task : tasks) task();
}

void KeyChannels::OnSessionLocked() {
  biometric_->InvokeMethod("screenLocked", nullptr);
}
