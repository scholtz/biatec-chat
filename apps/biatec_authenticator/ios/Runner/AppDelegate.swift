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
  /// Not an `override`: FlutterAppDelegate does not implement this optional UIApplicationDelegate method (see FlutterAppDelegate.mm), so
  /// `override` would not compile. `@objc` makes sure UIKit finds it under its Objective-C selector.
  @objc func application(
    _ application: UIApplication,
    shouldAllowExtensionPointIdentifier extensionPointIdentifier: UIApplication.ExtensionPointIdentifier
  ) -> Bool {
    return extensionPointIdentifier != .keyboard
  }

  /// Nothing the app stores in its own folders goes to an iCloud or iTunes backup (the vault and the history are device-only, BCHAT-009). The flag on a
  /// directory covers what is inside it, including what is created later (Apple: "excludes the directory and its contents"), so Library (which
  /// holds Application Support, Preferences and Caches, where the app and its plugins write files) and Documents are enough. The device key and
  /// the secure-storage items are not files: they live in the Secure Enclave and the keychain, kept off backups by their own accessibility class.
  /// Documents is flagged only once something created it; this runs on every launch.
  static func excludeStorageFromBackup() {
    let fm = FileManager.default
    let places: [FileManager.SearchPathDirectory] = [.libraryDirectory, .documentDirectory]
    for place in places {
      guard var url = fm.urls(for: place, in: .userDomainMask).first,
            fm.fileExists(atPath: url.path) else { continue }
      var values = URLResourceValues()
      values.isExcludedFromBackup = true
      do {
        try url.setResourceValues(values)
      } catch {
        // The next launch tries again. Only a debug build says so, and by folder name only: a release build logs nothing (as on Android).
        #if DEBUG
        NSLog("Biatec: could not exclude %@ from backup", url.lastPathComponent)
        #endif
      }
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
