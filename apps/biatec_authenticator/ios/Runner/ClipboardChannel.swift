import Flutter
import Foundation
import UIKit

/// Copies a one-time code to the pasteboard so it does not travel and does not stay (channel io.biatec.authenticator/clipboard).
///
/// The item is local only (no Universal Clipboard to other devices) and expires after the given number of seconds, on top of the Dart
/// side clearing it if it still holds the code. Nothing is logged.
public class ClipboardChannel: NSObject, FlutterPlugin {
    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/clipboard", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(ClipboardChannel(), channel: channel)
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        guard call.method == "copy" else { return result(FlutterMethodNotImplemented) }
        guard let args = call.arguments as? [String: Any], let text = args["text"] as? String else {
            return result(FlutterError(code: "bad_args", message: "text is required", details: nil))
        }
        let seconds = args["seconds"] as? Double ?? 30
        UIPasteboard.general.setItems(
            [["public.utf8-plain-text": text]],
            options: [.localOnly: true, .expirationDate: Date().addingTimeInterval(seconds)])
        result(nil)
    }
}
