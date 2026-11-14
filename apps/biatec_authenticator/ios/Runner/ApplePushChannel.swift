import Flutter
import Foundation
import UIKit
import UserNotifications

/// The optional Apple wake (channel io.biatec.authenticator/apns). Nothing here runs unless the Dart side calls `register`, and it does that only in a
/// build made with `--dart-define=APNS_ENABLED=true`; with the flag off the app never asks for notification permission and never registers for remote
/// notifications. No Firebase: the token is Apple's own device token, sent to Biatec OIDC with `PUT /devices/push {"provider":"apns","token":...}`.
///
/// The token (hex) identifies this app on this phone to Apple. A push carries no content; the device channel and the authenticated calls to Biatec OIDC
/// are how the app learns what a request is. A build with the flag on also needs the Push Notifications capability and the `aps-environment` entitlement
/// (ios/Runner/Runner-APNs.entitlements; see the README).
public class ApplePushChannel: NSObject, FlutterPlugin {
    private static var shared: ApplePushChannel?
    private let channel: FlutterMethodChannel
    private var pending: FlutterResult?

    init(channel: FlutterMethodChannel) {
        self.channel = channel
    }

    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/apns", binaryMessenger: registrar.messenger())
        let instance = ApplePushChannel(channel: channel)
        shared = instance
        registrar.addMethodCallDelegate(instance, channel: channel)
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        guard call.method == "register" else { return result(FlutterMethodNotImplemented) }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, _ in
            DispatchQueue.main.async {
                guard granted else { return result(nil) }
                // A registration that is still waiting for Apple is answered null; the newest call gets the token.
                self.pending?(nil)
                self.pending = result
                UIApplication.shared.registerForRemoteNotifications()
            }
        }
    }

    /// Called from the app delegate with the token Apple issued.
    static func didRegister(deviceToken: Data) {
        guard let instance = shared else { return }
        let hex = deviceToken.map { String(format: "%02x", $0) }.joined()
        if let waiting = instance.pending {
            instance.pending = nil
            waiting(hex)
        } else {
            // The system issued a new token on its own.
            instance.channel.invokeMethod("tokenRefreshed", arguments: hex)
        }
    }

    /// Called from the app delegate when Apple refused: the app then simply has no wake.
    static func didFail() {
        guard let instance = shared, let waiting = instance.pending else { return }
        instance.pending = nil
        waiting(nil)
    }
}
