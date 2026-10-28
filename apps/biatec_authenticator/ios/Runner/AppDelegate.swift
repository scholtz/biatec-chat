import Flutter
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    ScreenCover.shared.start()
    AppDelegate.excludeStorageFromBackup()
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  /// No third-party keyboard in the app: a custom keyboard can log what is typed (the binding code, a passphrase). The system keyboard only.
  override func application(
    _ application: UIApplication,
    shouldAllowExtensionPointIdentifier extensionPointIdentifier: UIApplication.ExtensionPointIdentifier
  ) -> Bool {
    return extensionPointIdentifier != .keyboard
  }

  /// Nothing the app stores in its own folders goes to an iCloud or iTunes backup (the vault and the history are device-only, BCHAT-009). The flag on a
  /// directory covers what is inside it. Keychain items are kept off backups by their own accessibility class (ThisDeviceOnly).
  static func excludeStorageFromBackup() {
    let fm = FileManager.default
    let places: [FileManager.SearchPathDirectory] = [.applicationSupportDirectory, .documentDirectory, .libraryDirectory]
    for place in places {
      guard var url = fm.urls(for: place, in: .userDomainMask).first else { continue }
      try? fm.createDirectory(at: url, withIntermediateDirectories: true)
      var values = URLResourceValues()
      values.isExcludedFromBackup = true
      try? url.setResourceValues(values)
    }
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
    // The device key lives in the Secure Enclave (KeystorePlugin); Dart reaches it over the channel io.biatec.authenticator/keystore.
    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "KeystorePlugin") {
      KeystorePlugin.register(with: registrar)
    }
  }
}
