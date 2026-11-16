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

#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>

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
  using Result = std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>>;

  // The answer of a worker: only data. The reply object stays on the window's thread (in `pending_`), so a worker that finishes after the window is gone has
  // nothing of the engine to touch.
  struct Done {
    uint64_t id = 0;
    flutter::EncodableValue value;
    std::string code;  // empty: success
    std::string message;
  };

  struct Shared {
    std::mutex mutex;
    std::deque<Done> queue;
    HWND window = nullptr;
  };

  // Runs `work` on a worker thread and answers `result` from the window's thread.
  void RunAsync(Result result, std::function<flutter::EncodableValue()> work);

  std::shared_ptr<Shared> shared_;
  std::map<uint64_t, Result> pending_;  // window's thread only
  uint64_t next_id_ = 1;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> keystore_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> biometric_;
};
