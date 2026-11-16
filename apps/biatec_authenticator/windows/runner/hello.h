// T-250: the Windows Hello check (face, fingerprint or the Hello PIN) through UserConsentVerifier, shown over the app's window.
//
// This is a yes or no for the app: Windows does not bind it to a key. The approval key's signature therefore runs only after this returns kVerified, inside
// the one native call (key_channels.cpp). The guarantee is the app's own, not the hardware's, and docs/authenticator-windows-key-store.md says so.
#pragma once

#include <windows.h>

#include <string>

namespace hello {

enum class Availability {
  kAvailable,
  kNotConfigured,  // Windows Hello is not set up for this user
  kNoDevice,       // no Windows Hello capable device
  kUnavailable,    // busy, disabled by policy, or anything else
};

enum class Outcome {
  kVerified,
  kCanceled,
  kRetriesExhausted,
  kNotConfigured,
  kNoDevice,
  kFailed,
};

// Whether Windows Hello can be used now. Blocks: call it from a worker thread.
Availability Check();

// Asks for Windows Hello with `message` over `window` and waits for the answer. Blocks until the user answers: call it from a worker thread, never from the
// UI thread (the prompt needs the window's message loop). Anything unexpected is kFailed.
Outcome Verify(HWND window, const std::wstring& message);

}  // namespace hello
