#include "hello.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Security.Credentials.UI.h>
#include <winrt/base.h>

#include <UserConsentVerifierInterop.h>

namespace hello {
namespace {

namespace ui = winrt::Windows::Security::Credentials::UI;

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
    switch (ui::UserConsentVerifier::CheckAvailabilityAsync().get()) {
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
    switch (operation.get()) {
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
