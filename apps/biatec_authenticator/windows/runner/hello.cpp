#include "hello.h"

#include <atomic>
#include <chrono>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Security.Credentials.UI.h>
#include <winrt/base.h>

#include <UserConsentVerifierInterop.h>

namespace hello {
namespace {

namespace ui = winrt::Windows::Security::Credentials::UI;

// True from the start of a consent dialog until its operation ends.
std::atomic<bool> g_dialog_open{false};

constexpr std::chrono::minutes kPromptTimeout{2};
constexpr std::chrono::seconds kCheckTimeout{10};
constexpr std::chrono::seconds kCancelGrace{5};

// A worker thread joins the multithreaded apartment once; the main thread is a single-threaded apartment and does not call in here.
void EnsureApartment() {
  thread_local bool initialized = false;
  if (!initialized) {
    try {
      winrt::init_apartment(winrt::apartment_type::multi_threaded);
    } catch (const winrt::hresult_error&) {
      // Already initialized in another mode: the calls below still work.
    }
    initialized = true;
  }
}

}  // namespace

Availability Check() {
  try {
    EnsureApartment();
    auto check = ui::UserConsentVerifier::CheckAvailabilityAsync();
    // The same rule as for the prompt: a call that never completes must not hold the worker (and the enrollment waiting for the probe) for ever.
    const auto checked = check.wait_for(kCheckTimeout);
    if (checked == winrt::Windows::Foundation::AsyncStatus::Started) {
      check.Cancel();
      return Availability::kUnavailable;
    }
    if (checked != winrt::Windows::Foundation::AsyncStatus::Completed) {
      return Availability::kUnavailable;  // it failed or was cancelled
    }
    switch (check.GetResults()) {
      case ui::UserConsentVerifierAvailability::Available:
        return Availability::kAvailable;
      case ui::UserConsentVerifierAvailability::NotConfiguredForUser:
        return Availability::kNotConfigured;
      case ui::UserConsentVerifierAvailability::DeviceNotPresent:
        return Availability::kNoDevice;
      default:
        return Availability::kUnavailable;
    }
  } catch (...) {
    return Availability::kUnavailable;
  }
}

Outcome Verify(HWND window, const std::wstring& message) {
  // A dialog of an earlier prompt that was cancelled but has not closed yet is still on the screen: no second one is opened over it.
  if (g_dialog_open.load()) return Outcome::kFailed;
  try {
    EnsureApartment();
    auto factory = winrt::get_activation_factory<ui::UserConsentVerifier,
                                                 IUserConsentVerifierInterop>();
    winrt::Windows::Foundation::IAsyncOperation<ui::UserConsentVerificationResult>
        operation{nullptr};
    const winrt::hstring text{message};
    winrt::check_hresult(factory->RequestVerificationForWindowAsync(
        window, static_cast<HSTRING>(winrt::get_abi(text)),
        winrt::guid_of<winrt::Windows::Foundation::IAsyncOperation<
            ui::UserConsentVerificationResult>>(),
        winrt::put_abi(operation)));
    // The dialog counts as open until the operation really ends, whatever this function returns meanwhile.
    g_dialog_open = true;
    operation.Completed([](const auto&, winrt::Windows::Foundation::AsyncStatus) {
      g_dialog_open = false;
    });
    // A prompt nobody answers must not hold the worker (and with it every later approval) for ever: after this time it is taken as cancelled.
    const auto answered = operation.wait_for(kPromptTimeout);
    if (answered == winrt::Windows::Foundation::AsyncStatus::Started) {
      operation.Cancel();
      // The dialog is closed by the cancel: wait for that (briefly). If it is slow, g_dialog_open keeps a second prompt from opening over it.
      operation.wait_for(kCancelGrace);
      return Outcome::kCanceled;
    }
    if (answered == winrt::Windows::Foundation::AsyncStatus::Error) return Outcome::kFailed;
    if (answered == winrt::Windows::Foundation::AsyncStatus::Canceled) return Outcome::kCanceled;
    switch (operation.GetResults()) {
      case ui::UserConsentVerificationResult::Verified:
        return Outcome::kVerified;
      case ui::UserConsentVerificationResult::Canceled:
        return Outcome::kCanceled;
      case ui::UserConsentVerificationResult::RetriesExhausted:
        return Outcome::kRetriesExhausted;
      case ui::UserConsentVerificationResult::NotConfiguredForUser:
        return Outcome::kNotConfigured;
      case ui::UserConsentVerificationResult::DeviceNotPresent:
        return Outcome::kNoDevice;
      default:
        return Outcome::kFailed;
    }
  } catch (...) {
    // Fails closed: nothing unexpected is a "verified".
    return Outcome::kFailed;
  }
}

}  // namespace hello
