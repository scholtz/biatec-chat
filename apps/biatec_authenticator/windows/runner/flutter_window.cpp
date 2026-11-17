#include "flutter_window.h"

#include <optional>

#include <wtsapi32.h>

#include "flutter/generated_plugin_registrant.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  key_channels_ = std::make_unique<KeyChannels>(
      flutter_controller_->engine()->messenger(), GetHandle());
  // So the app can lock itself when Windows locks (the biometric channel's screenLocked).
  session_notifications_ =
      ::WTSRegisterSessionNotification(GetHandle(), NOTIFY_FOR_THIS_SESSION) != FALSE;
  if (!session_notifications_) {
    // Windows would not tell the app when the session locks: the app then locks by its own timer only. Said where a developer looks.
    ::OutputDebugStringW(L"biatec_authenticator: WTSRegisterSessionNotification failed; the app will not lock when Windows locks.\n");
  }
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  if (session_notifications_) {
    ::WTSUnRegisterSessionNotification(GetHandle());
    session_notifications_ = false;
  }
  key_channels_ = nullptr;
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  if (key_channels_) {
    if (message == KeyChannels::kRunQueuedMessage) {
      key_channels_->RunQueued();
      return 0;
    }
    // Locked, or the user's session is no longer in front of them (a remote desktop client closed, a switch of user, a logoff): the app locks itself.
    if (message == WM_WTSSESSION_CHANGE &&
        (wparam == WTS_SESSION_LOCK || wparam == WTS_REMOTE_DISCONNECT ||
         wparam == WTS_CONSOLE_DISCONNECT || wparam == WTS_SESSION_LOGOFF)) {
      key_channels_->OnSessionLocked();
    }
  }

  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
