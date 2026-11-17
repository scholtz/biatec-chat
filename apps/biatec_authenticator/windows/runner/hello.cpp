#include "hello.h"

#include <chrono>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Security.Credentials.UI.h>
#include <winrt/base.h>

#include <UserConsentVerifierInterop.h>

namespace hello {
namespace {

namespace ui = winrt::Windows::Security::Credentials::UI;

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
    if (check.wait_for(kCheckTimeout) != winrt::Windows::Foundation::AsyncStatus::Completed) {
      check.Cancel();
      return Availability::kUnavailable;
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
    // A prompt nobody answers must not hold the worker (and with it every later approval) for ever: after this time it is taken as cancelled.
    if (operation.wait_for(kPromptTimeout) !=
        winrt::Windows::Foundation::AsyncStatus::Completed) {
      operation.Cancel();
      // The dialog is closed by the cancel: wait for that (briefly), so a second prompt is not opened while the first one is still on the screen.
      operation.wait_for(kCancelGrace);
      return Outcome::kCanceled;
    }
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
