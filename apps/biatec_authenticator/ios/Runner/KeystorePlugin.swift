import Flutter
import Foundation
import LocalAuthentication
import Security

/// Secure Enclave implementation of the device key channel (io.biatec.authenticator/keystore): TWO keys (protocol 1.1).
///
///  - Approval key: access control `.privateKeyUsage` + `.biometryCurrentSet` (a biometric of the CURRENT set for every use; the key is
///    invalidated when a fingerprint or face is added or removed), `kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly`. Signing uses an
///    `LAContext` whose prompt text is the app's fixed string. There is no `.devicePasscode` and no `.userPresence`: the passcode can never
///    stand in for the biometric, and there is no software fallback.
///  - Presence key: `.privateKeyUsage` with `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`, no user authentication.
///
/// Both are non-exportable (`kSecAttrTokenIDSecureEnclave`). The Secure Enclave is absent on the simulator: generateKeys fails with
/// `no_secure_enclave`. NOTE: this file has never been compiled or run on a device (see the README); nothing here logs a key, a tag or an error text.
public class KeystorePlugin: NSObject, FlutterPlugin {
    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/keystore", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(KeystorePlugin(), channel: channel)
    }

    private static let approvalTag = "io.biatec.authenticator.key.approval.v2"
    private static let presenceTag = "io.biatec.authenticator.key.presence.v2"
    /// Set when the approval key was made here, so a key that is gone (iOS removes it when the biometric set changes) is reported as invalidated, not as a fresh install.
    private static let markerKey = "io.biatec.authenticator.approval_key_created"

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        let args = call.arguments as? [String: Any] ?? [:]
        let method = call.method
        // Secure Enclave key generation and signing can block for a while (and the biometric prompt waits for the user): off the main thread, with the result posted back to it.
        DispatchQueue.global(qos: .userInitiated).async {
            self.run(method, args) { value in DispatchQueue.main.async { result(value) } }
        }
    }

    private func run(_ method: String, _ args: [String: Any], _ result: (Any?) -> Void) {
        switch method {
        case "generateKeys":
            generateKeys(result)
        case "load":
            switch args["use"] as? String {
            case "approval": result(parts(KeystorePlugin.approvalTag, approval: true))
            case "presence": result(parts(KeystorePlugin.presenceTag, approval: false))
            default: result(FlutterError(code: "bad_args", message: "use is required", details: nil))
            }
        case "keysState":
            result(keysState())
        case "signPresence":
            guard let data = (args["data"] as? FlutterStandardTypedData)?.data, let key = privateKey(KeystorePlugin.presenceTag, context: nil) else {
                return result(FlutterError(code: "no_key", message: "No key or data", details: nil))
            }
            sign(key, data, result)
        case "signApproval":
            signApproval(args, result)
        case "deleteKeys":
            deleteKeys()
            result(nil)
        case "delete":
            // The single key of an install from before the two keys, by its tag.
            if let alias = args["alias"] as? String, !alias.isEmpty,
               alias != KeystorePlugin.approvalTag, alias != KeystorePlugin.presenceTag {
                SecItemDelete(query(alias) as CFDictionary)
            }
            result(nil)
        default:
            result(FlutterMethodNotImplemented)
        }
    }

    // MARK: Signing

    private func sign(_ key: SecKey, _ data: Data, _ result: (Any?) -> Void) {
        var err: Unmanaged<CFError>?
        guard let der = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &err) as Data? else {
            return result(FlutterError(code: "keystore_error", message: "The keystore could not sign", details: nil))
        }
        result(FlutterStandardTypedData(bytes: derToRaw(der)))
    }

    /// The approval signature: the Secure Enclave releases the key for this one operation only after the system verified a biometric of the current set, with the fixed prompt text below. The call blocks until the user answers.
    private func signApproval(_ args: [String: Any], _ result: (Any?) -> Void) {
        guard let data = (args["data"] as? FlutterStandardTypedData)?.data else {
            return result(FlutterError(code: "bad_args", message: "data is required", details: nil))
        }
        let context = LAContext()
        // The app's fixed text (the title it passes), not text a request supplies; the client and total are in the card the user just read.
        context.localizedReason = (args["title"] as? String).flatMap { $0.isEmpty ? nil : String($0.prefix(80)) } ?? "Approve this action"
        context.localizedFallbackTitle = ""      // no "Enter passcode" button: the passcode cannot approve
        context.localizedCancelTitle = "Cancel"
        guard let key = privateKey(KeystorePlugin.approvalTag, context: context) else {
            return result(FlutterError(code: UserDefaults.standard.bool(forKey: KeystorePlugin.markerKey) ? "approval_key_invalidated" : "no_key",
                                       message: "The approval key is gone", details: nil))
        }
        var err: Unmanaged<CFError>?
        guard let der = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &err) as Data? else {
            let code = err.map { CFErrorGetCode($0.takeRetainedValue()) } ?? 0
            switch Int32(truncatingIfNeeded: code) {
            case errSecUserCanceled, Int32(LAError.userCancel.rawValue), Int32(LAError.appCancel.rawValue), Int32(LAError.systemCancel.rawValue):
                return result(FlutterError(code: "cancelled", message: "The biometric check was cancelled", details: nil))
            case Int32(LAError.biometryLockout.rawValue):
                return result(FlutterError(code: "locked_out", message: "Too many tries. Unlock the phone with its passcode, then try again.", details: nil))
            case Int32(LAError.biometryNotEnrolled.rawValue), Int32(LAError.biometryNotAvailable.rawValue):
                return result(FlutterError(code: "no_biometrics", message: "No biometric is available on this phone", details: nil))
            case errSecItemNotFound:
                return result(FlutterError(code: "approval_key_invalidated", message: "The approval key was invalidated", details: nil))
            default:
                return result(FlutterError(code: "auth_failed", message: "The biometric check did not succeed", details: nil))
            }
        }
        result(FlutterStandardTypedData(bytes: derToRaw(der)))
    }

    // MARK: Keys

    private func tag(_ alias: String) -> Data { Data(alias.utf8) }

    private func query(_ alias: String) -> [String: Any] {
        [kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag(alias), kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom]
    }

    /// The key reference. With a [context] the biometric prompt of a later signature uses it; with no context, an approval key is never touched here for signing.
    private func privateKey(_ alias: String, context: LAContext?) -> SecKey? {
        var q = query(alias)
        q[kSecReturnRef as String] = true
        if let context = context { q[kSecUseAuthenticationContext as String] = context }
        var item: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess ? (item as! SecKey) : nil
    }

    private func access(approval: Bool) -> SecAccessControl? {
        var err: Unmanaged<CFError>?
        if approval {
            return SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly, [.privateKeyUsage, .biometryCurrentSet], &err)
        }
        return SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, .privateKeyUsage, &err)
    }

    private func make(_ alias: String, approval: Bool) -> (SecKey?, String?) {
        guard let access = access(approval: approval) else { return (nil, approval ? "no_biometrics" : "keystore_error") }
        var err: Unmanaged<CFError>?
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
            kSecPrivateKeyAttrs as String: [kSecAttrIsPermanent as String: true, kSecAttrApplicationTag as String: tag(alias), kSecAttrAccessControl as String: access],
        ]
        guard let key = SecKeyCreateRandomKey(attrs as CFDictionary, &err) else {
            // `.biometryCurrentSet` needs an enrolled biometric and a passcode: without them the key cannot be made.
            _ = err?.takeRetainedValue()
            return (nil, approval ? "no_biometrics" : "no_secure_enclave")
        }
        return (key, nil)
    }

    private func generateKeys(_ result: (Any?) -> Void) {
        if let a = parts(KeystorePlugin.approvalTag, approval: true), let p = parts(KeystorePlugin.presenceTag, approval: false),
           a["authBound"] as? Bool == true {
            return result(["approval": a, "presence": p])
        }
        // Both or neither: a half set is removed and made again.
        deleteKeys()
        let (approvalKey, approvalError) = make(KeystorePlugin.approvalTag, approval: true)
        guard approvalKey != nil else {
            return result(FlutterError(code: approvalError ?? "keystore_error", message: "The approval key could not be created. A fingerprint or face and a passcode must be set up on this phone.", details: nil))
        }
        UserDefaults.standard.set(true, forKey: KeystorePlugin.markerKey)
        let (presenceKey, presenceError) = make(KeystorePlugin.presenceTag, approval: false)
        guard presenceKey != nil else {
            deleteKeys()
            return result(FlutterError(code: presenceError ?? "keystore_error", message: "The presence key could not be created", details: nil))
        }
        guard let a = parts(KeystorePlugin.approvalTag, approval: true), let p = parts(KeystorePlugin.presenceTag, approval: false) else {
            deleteKeys()
            return result(FlutterError(code: "keystore_error", message: "The public key has an unexpected shape", details: nil))
        }
        // Refuse unless the attributes say the approval key is a Secure Enclave key with the biometric access control.
        if a["authBound"] as? Bool != true {
            deleteKeys()
            return result(FlutterError(code: "insecure_key", message: "This phone cannot protect approvals", details: ["securityLevel": a["securityLevel"] ?? "unknown"]))
        }
        result(["approval": a, "presence": p])
    }

    private func deleteKeys() {
        SecItemDelete(query(KeystorePlugin.approvalTag) as CFDictionary)
        SecItemDelete(query(KeystorePlugin.presenceTag) as CFDictionary)
        UserDefaults.standard.removeObject(forKey: KeystorePlugin.markerKey)
    }

    /// Without a prompt: asking with an LAContext that may not interact answers errSecInteractionNotAllowed for a key that exists and is usable, and errSecItemNotFound once the biometric set changed.
    private func keysState() -> String {
        let presence = privateKey(KeystorePlugin.presenceTag, context: nil) != nil
        let context = LAContext()
        context.interactionNotAllowed = true
        var q = query(KeystorePlugin.approvalTag)
        q[kSecReturnRef as String] = true
        q[kSecUseAuthenticationContext as String] = context
        var item: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &item)
        if status == errSecItemNotFound {
            return UserDefaults.standard.bool(forKey: KeystorePlugin.markerKey) ? "approval_key_invalidated" : "missing"
        }
        return presence ? "ok" : "missing"
    }

    /// The public key as the channel returns it, or nil when there is no such key.
    private func parts(_ alias: String, approval: Bool) -> [String: Any]? {
        // The public half is read from the key reference, which never prompts.
        guard let key = privateKey(alias, context: nil), let pub = SecKeyCopyPublicKey(key),
              let raw = SecKeyCopyExternalRepresentation(pub, nil) as Data?, raw.count == 65, raw[0] == 4 else { return nil }
        let level = securityLevel(key)
        return [
            "x": b64(raw.subdata(in: 1..<33)),
            "y": b64(raw.subdata(in: 33..<65)),
            "securityLevel": level,
            // The key is a Secure Enclave key made with the biometry-current-set access control (this plugin made it; the Secure Enclave does not let it be made any other way under the same tag).
            "authBound": approval && level == "secure_enclave",
        ]
    }

    private func b64(_ d: Data) -> String {
        d.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    /// "secure_enclave" when the key's token is the Secure Enclave, "software" for any other key, "unknown" when the attributes cannot be read.
    private func securityLevel(_ key: SecKey) -> String {
        guard let attrs = SecKeyCopyAttributes(key) as? [String: Any], let token = attrs[kSecAttrTokenID as String] as? String else { return "unknown" }
        return token == (kSecAttrTokenIDSecureEnclave as String) ? "secure_enclave" : "software"
    }

    /// DER ECDSA signature to raw r||s (64 bytes).
    private func derToRaw(_ der: Data) -> Data {
        let b = [UInt8](der)
        var i = 2
        if b[1] & 0x80 != 0 { i += Int(b[1] & 0x7f) }
        func int() -> [UInt8] {
            let len = Int(b[i + 1]); let v = Array(b[(i + 2)..<(i + 2 + len)]); i += 2 + len
            let t = Array(v.drop(while: { $0 == 0 }))
            return [UInt8](repeating: 0, count: 32 - t.count) + t
        }
        let r = int(); let s = int()
        return Data(r + s)
    }
}
