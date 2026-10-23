import Flutter
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    ScreenCover.shared.start()
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
    // The device key lives in the Secure Enclave (KeystorePlugin); Dart reaches it over the channel io.biatec.authenticator/keystore.
    if let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "KeystorePlugin") {
      KeystorePlugin.register(with: registrar)
    }
  }
}
