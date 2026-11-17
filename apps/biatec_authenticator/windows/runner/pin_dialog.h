// T-250: the app's own PIN, asked in a native dialog over the app's window. It is used on a computer where Windows Hello cannot be used.
//
// The PIN is not checked here and is not kept anywhere by the app: it is the usage secret of keys in the TPM (keystore.h), so the TPM itself refuses to sign
// without it and locks after too many wrong tries. The PIN goes from this dialog straight to the key store, inside the one native call; it never crosses the
// channel to the Dart side.
#pragma once

#include <windows.h>

#include <optional>
#include <string>

namespace pin_dialog {

// A PIN in memory, wiped when it goes away.
class Pin {
 public:
  // Copies, and the caller wipes its own copy: a moved-from string can keep its characters in its small buffer.
  explicit Pin(const std::wstring& value) : value_(value) {}
  Pin(Pin&& other) : value_(other.value_) { other.Wipe(); }
  Pin(const Pin&) = delete;
  Pin& operator=(const Pin&) = delete;
  ~Pin() { Wipe(); }
  const std::wstring& value() const { return value_; }

 private:
  void Wipe() {
    if (!value_.empty()) ::SecureZeroMemory(value_.data(), value_.size() * sizeof(wchar_t));
    value_.clear();
  }
  std::wstring value_;
};

enum class Mode {
  kCreate,  // a new PIN, typed twice
  kEnter,   // the existing PIN
};

constexpr size_t kMinLength = 6;
constexpr size_t kMaxLength = 64;

// Shows the dialog over `owner` with `message` above the field and waits for the answer. Nothing when the user closed it, or did not answer in time. Blocks: call
// it from a worker thread (the dialog runs its own message loop there, and the app's window keeps painting).
std::optional<Pin> Ask(HWND owner, Mode mode, const std::wstring& message);

// The text shown when a new PIN is chosen, in the language of the Windows user interface (English or Slovak).
std::wstring CreateMessage();

}  // namespace pin_dialog
