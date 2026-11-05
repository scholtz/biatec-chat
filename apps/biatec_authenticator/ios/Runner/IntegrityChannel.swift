import CryptoKit
import DeviceCheck
import Flutter
import Foundation

/// The facts the Dart integrity detectors cannot read themselves, and App Attest (channels io.biatec.authenticator/integrity and
/// io.biatec.authenticator/attestation). Files are read in Dart; the policy is in Dart; nothing here decides, and nothing is logged.
///
/// integrity: debuggerAttached | loadedImages | isSimulator
/// attestation: token(nonce) -> {kind: "app_attest", token, keyId}
public class IntegrityChannel: NSObject, FlutterPlugin {
    public static func register(with registrar: FlutterPluginRegistrar) {
        let integrity = FlutterMethodChannel(name: "io.biatec.authenticator/integrity", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(IntegrityChannel(), channel: integrity)
        let attestation = FlutterMethodChannel(name: "io.biatec.authenticator/attestation", binaryMessenger: registrar.messenger())
        attestation.setMethodCallHandler { call, result in
            guard call.method == "token" else { return result(FlutterMethodNotImplemented) }
            guard let nonce = (call.arguments as? [String: Any])?["nonce"] as? String else {
                return result(FlutterError(code: "bad_args", message: "nonce is required", details: nil))
            }
            IntegrityChannel.attest(nonce: nonce, result: result)
        }
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        switch call.method {
        case "debuggerAttached": result(IntegrityChannel.debuggerAttached())
        case "loadedImages": result(IntegrityChannel.loadedImages())
        case "isSimulator":
            #if targetEnvironment(simulator)
            result(true)
            #else
            result(false)
            #endif
        default: result(FlutterMethodNotImplemented)
        }
    }

    /// The P_TRACED flag of this process, as a debugger sets it.
    private static func debuggerAttached() -> Bool {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        guard sysctl(&mib, UInt32(mib.count), &info, &size, nil, 0) == 0 else { return false }
        return (info.kp_proc.p_flag & P_TRACED) != 0
    }

    /// The paths of the libraries loaded into the process, so an injected one shows.
    private static func loadedImages() -> [String] {
        (0..<_dyld_image_count()).compactMap { index in _dyld_get_image_name(index).map { String(cString: $0) } }
    }

    /// App Attest: a new key, attested over the SHA-256 of the server's nonce. The key id and the attestation go to Biatec OIDC to verify.
    private static func attest(nonce: String, result: @escaping FlutterResult) {
        let service = DCAppAttestService.shared
        guard service.isSupported else {
            return result(FlutterError(code: "unavailable", message: "App Attest is not available", details: nil))
        }
        service.generateKey { keyId, error in
            guard let keyId = keyId, error == nil else {
                return DispatchQueue.main.async { result(FlutterError(code: "unavailable", message: "No attestation key", details: nil)) }
            }
            let hash = Data(SHA256.hash(data: Data(nonce.utf8)))
            service.attestKey(keyId, clientDataHash: hash) { attestation, error in
                DispatchQueue.main.async {
                    guard let attestation = attestation, error == nil else {
                        return result(FlutterError(code: "unavailable", message: "No attestation", details: nil))
                    }
                    result(["kind": "app_attest", "token": attestation.base64EncodedString(), "keyId": keyId])
                }
            }
        }
    }
}
