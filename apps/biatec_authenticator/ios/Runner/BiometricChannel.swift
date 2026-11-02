import Flutter
import Foundation
import LocalAuthentication
import UIKit

/// The app's biometric lock (channel io.biatec.authenticator/biometric): Face ID or Touch ID and nothing else.
///
/// The policy is `deviceOwnerAuthenticationWithBiometrics`, which never offers the passcode, and the fallback button is hidden
/// (`localizedFallbackTitle = ""`). The device passcode cannot open the app. Nothing here logs a result or an error text.
///
/// status -> available | none_enrolled | no_hardware | unavailable
/// authenticate(reason) -> success | cancelled | failed | lockout | none_enrolled | unavailable
/// Calls screenLocked on the channel when protected data becomes unavailable (the screen locked).
public class BiometricChannel: NSObject, FlutterPlugin {
    private let channel: FlutterMethodChannel

    init(channel: FlutterMethodChannel) {
        self.channel = channel
        super.init()
        // The device locking is a lock of the app, whatever the background delay is.
        NotificationCenter.default.addObserver(
            self, selector: #selector(deviceLocked),
            name: UIApplication.protectedDataWillBecomeUnavailableNotification, object: nil)
    }

    deinit { NotificationCenter.default.removeObserver(self) }

    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/biometric", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(BiometricChannel(channel: channel), channel: channel)
    }

    @objc private func deviceLocked() {
        DispatchQueue.main.async { self.channel.invokeMethod("screenLocked", arguments: nil) }
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        switch call.method {
        case "status":
            result(status())
        case "authenticate":
            authenticate(reason: (call.arguments as? [String: Any])?["reason"] as? String ?? "Unlock", result: result)
        default:
            result(FlutterMethodNotImplemented)
        }
    }

    private func newContext() -> LAContext {
        let context = LAContext()
        context.localizedFallbackTitle = ""
        return context
    }

    private func status() -> String {
        var error: NSError?
        if newContext().canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) { return "available" }
        switch (error as? LAError)?.code {
        case .biometryNotEnrolled?, .passcodeNotSet?: return "none_enrolled"
        case .biometryNotAvailable?: return "no_hardware"
        default: return "unavailable"
        }
    }

    private func authenticate(reason: String, result: @escaping FlutterResult) {
        let context = newContext()
        var error: NSError?
        guard context.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &error) else {
            switch (error as? LAError)?.code {
            case .biometryNotEnrolled?, .passcodeNotSet?: return result("none_enrolled")
            case .biometryLockout?: return result("lockout")
            default: return result("unavailable")
            }
        }
        context.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, localizedReason: reason) { ok, err in
            let answer: String
            if ok {
                answer = "success"
            } else {
                switch (err as? LAError)?.code {
                case .userCancel?, .systemCancel?, .appCancel?: answer = "cancelled"
                case .biometryLockout?: answer = "lockout"
                case .biometryNotEnrolled?: answer = "none_enrolled"
                case .biometryNotAvailable?: answer = "unavailable"
                default: answer = "failed"
                }
            }
            DispatchQueue.main.async { result(answer) }
        }
    }
}
