// T-250: the native side of two platform channels of the authenticator, for Windows.
//
//  * `io.biatec.authenticator/keystore`: the device keys in the TPM (keystore.h). Same methods as KeystorePlugin.kt and KeystorePlugin.swift, plus `probe`.
//  * `io.biatec.authenticator/biometric`: Windows Hello for the app lock and for the approvals of the single-key mode (hello.h), and `screenLocked`.
//
// The CNG and Windows Hello calls block (a Hello prompt waits for the user), so they run on a worker thread and the answer is posted back to the window's
// thread, where Flutter's channel objects may be used.
#pragma once

#include <flutter/binary_messenger.h>
#include <flutter/encodable_value.h>
#include <flutter/method_channel.h>
#include <windows.h>

#include <deque>
#include <functional>
#include <memory>
#include <mutex>

class KeyChannels {
 public:
  // The window message that tells the window to run the answers workers have queued (the window calls RunQueued for it).
  static constexpr UINT kRunQueuedMessage = WM_APP + 0x42;

  KeyChannels(flutter::BinaryMessenger* messenger, HWND window);
  ~KeyChannels();
  KeyChannels(const KeyChannels&) = delete;
  KeyChannels& operator=(const KeyChannels&) = delete;

  // Runs the queued answers. On the window's thread.
  void RunQueued();

  // The Windows session was locked: tells the app, which locks itself.
  void OnSessionLocked();

 private:
  struct Shared {
    std::mutex mutex;
    std::deque<std::function<void()>> queue;
    HWND window = nullptr;
  };

  std::shared_ptr<Shared> shared_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> keystore_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> biometric_;
};
