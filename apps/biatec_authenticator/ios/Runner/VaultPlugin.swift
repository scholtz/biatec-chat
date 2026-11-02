import CryptoKit
import Flutter
import Foundation
import LocalAuthentication
import Security

/// Secure Enclave sealing for the two-factor vault (channel io.biatec.authenticator/vault).
///
/// Two P-256 keys are created inside the Secure Enclave (kSecAttrTokenIDSecureEnclave), so they cannot be exported:
///  - metadata: usable without the user, so the account list shows before the user proves anything;
///  - secret: access control `biometryCurrentSet` (a prompt for every use, and the key stops working when a fingerprint or face is added or
///    removed), accessible only on this device (`ThisDeviceOnly`), never in a backup.
/// A value is sealed with ECIES (`eciesEncryptionCofactorVariableIVX963SHA256AESGCM`: a fresh ephemeral key and a fresh AES-GCM nonce for every
/// seal). That scheme has no associated-data input, so the associated data goes in as a SHA-256 prefix of the plaintext and is compared when the
/// value is opened: a sealed value only opens for the account id it was sealed for. Nothing here logs a key, a secret or an error text.
public class VaultPlugin: NSObject, FlutterPlugin {
    private static let algorithm = SecKeyAlgorithm.eciesEncryptionCofactorVariableIVX963SHA256AESGCM

    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/vault", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(VaultPlugin(), channel: channel)
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        let method = call.method
        let args = call.arguments as? [String: Any]
        // Secure Enclave work and the biometric prompt block: off the main thread, with the result posted back to it.
        DispatchQueue.global(qos: .userInitiated).async {
            let value = self.run(method, args)
            DispatchQueue.main.async { result(value) }
        }
    }

    private func run(_ method: String, _ args: [String: Any]?) -> Any? {
        switch method {
        case "endSession":
            // The app locked: the authenticated context is dropped, so the next secret read asks for Face ID or Touch ID again.
            sharedContext?.invalidate()
            sharedContext = nil
            return nil
        case "deleteKeys":
            for purpose in ["metadata", "secret"] {
                SecItemDelete(keyQuery(purpose) as CFDictionary)
                SecItemDelete(markerQuery(purpose) as CFDictionary)
            }
            return nil
        case "seal", "open":
            guard let purpose = args?["purpose"] as? String, purpose == "metadata" || purpose == "secret",
                  let data = (args?["data"] as? FlutterStandardTypedData)?.data,
                  let aad = (args?["aad"] as? FlutterStandardTypedData)?.data else {
                return FlutterError(code: "bad_args", message: "purpose, data and aad are required", details: nil)
            }
            return method == "seal" ? seal(purpose, data, aad) : open(purpose, data, aad)
        default:
            return FlutterMethodNotImplemented
        }
    }

    // MARK: keys

    private func tag(_ purpose: String) -> Data { Data("io.biatec.authenticator.vault.\(purpose)".utf8) }

    private func keyQuery(_ purpose: String) -> [String: Any] {
        [kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag(purpose), kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom]
    }

    /// A keychain item that says the key for [purpose] was created here, so a key that has gone missing since (the biometric set changed) is told apart from one never made.
    private func markerQuery(_ purpose: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "io.biatec.authenticator.vault.marker", kSecAttrAccount as String: purpose]
    }

    private func markerExists(_ purpose: String) -> Bool {
        SecItemCopyMatching(markerQuery(purpose) as CFDictionary, nil) == errSecSuccess
    }

    private func writeMarker(_ purpose: String) {
        var item = markerQuery(purpose)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        item[kSecValueData as String] = Data([1])
        SecItemAdd(item as CFDictionary, nil)
    }

    /// One authenticated context is reused for 30 seconds, so the codes of every account open after a single Face ID or Touch ID check instead of one per account.
    private var sharedContext: LAContext?
    private var sharedContextMade = Date.distantPast

    private func currentContext() -> LAContext {
        if let context = sharedContext, Date().timeIntervalSince(sharedContextMade) < 30 { return context }
        let context = LAContext()
        context.localizedReason = "Unlock your two-factor accounts"
        context.touchIDAuthenticationAllowableReuseDuration = 30
        sharedContext = context
        sharedContextMade = Date()
        return context
    }

    private func existingKey(_ purpose: String, _ context: LAContext?) -> SecKey? {
        var q = keyQuery(purpose)
        q[kSecReturnRef as String] = true
        if let context = context { q[kSecUseAuthenticationContext as String] = context }
        var item: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess ? (item as! SecKey) : nil
    }

    private func createKey(_ purpose: String) -> SecKey? {
        var flags: SecAccessControlCreateFlags = [.privateKeyUsage]
        if purpose == "secret" { flags.insert(.biometryCurrentSet) }
        guard let access = SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, flags, nil) else { return nil }
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
            kSecPrivateKeyAttrs as String: [
                kSecAttrIsPermanent as String: true,
                kSecAttrApplicationTag as String: tag(purpose),
                kSecAttrAccessControl as String: access,
            ],
        ]
        guard let key = SecKeyCreateRandomKey(attrs as CFDictionary, nil) else { return nil }
        writeMarker(purpose)
        return key
    }

    // MARK: seal and open

    private func prefix(_ aad: Data) -> Data { Data(SHA256.hash(data: aad)) }

    private func seal(_ purpose: String, _ data: Data, _ aad: Data) -> Any? {
        var key = existingKey(purpose, nil)
        if key == nil {
            // A key that was made here and is gone was invalidated by a change of biometrics; a new one is not made in its place, or the old vault would be unreadable.
            if markerExists(purpose) { return FlutterError(code: "biometrics_changed", message: "The vault key was invalidated", details: nil) }
            key = createKey(purpose)
        }
        guard let privateKey = key, let publicKey = SecKeyCopyPublicKey(privateKey),
              SecKeyIsAlgorithmSupported(publicKey, .encrypt, VaultPlugin.algorithm) else {
            return FlutterError(code: "keystore_error", message: "The keystore could not do that", details: nil)
        }
        var plain = prefix(aad) + data
        defer { plain.resetBytes(in: 0..<plain.count) }
        guard let sealed = SecKeyCreateEncryptedData(publicKey, VaultPlugin.algorithm, plain as CFData, nil) as Data? else {
            return FlutterError(code: "keystore_error", message: "The keystore could not do that", details: nil)
        }
        return FlutterStandardTypedData(bytes: sealed)
    }

    private func open(_ purpose: String, _ sealed: Data, _ aad: Data) -> Any? {
        let context = currentContext()
        guard let key = existingKey(purpose, context) else {
            if markerExists(purpose) { return FlutterError(code: "biometrics_changed", message: "The vault key was invalidated", details: nil) }
            return FlutterError(code: "bad_data", message: "No key for this value", details: nil)
        }
        var err: Unmanaged<CFError>?
        guard var plain = SecKeyCreateDecryptedData(key, VaultPlugin.algorithm, sealed as CFData, &err) as Data? else {
            let failure = err?.takeRetainedValue()
            // LocalAuthentication errors (cancelled, locked out, no biometry) are an auth failure the user can retry, never a damaged vault.
            if let failure = failure, CFErrorGetDomain(failure) as String == LAError.errorDomain {
                return FlutterError(code: "auth_failed", message: "The biometric check did not succeed", details: nil)
            }
            let code = failure.map { CFErrorGetCode($0) } ?? 0
            switch Int32(truncatingIfNeeded: code) {
            case errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed, errSecNotAvailable:
                return FlutterError(code: "auth_failed", message: "The biometric check did not succeed", details: nil)
            case errSecItemNotFound:
                return FlutterError(code: "biometrics_changed", message: "The vault key was invalidated", details: nil)
            default:
                return FlutterError(code: "bad_data", message: "A sealed value did not verify", details: nil)
            }
        }
        defer { plain.resetBytes(in: 0..<plain.count) }
        let expected = prefix(aad)
        guard plain.count >= expected.count else {
            return FlutterError(code: "bad_data", message: "A sealed value did not verify", details: nil)
        }
        // Compared without stopping at the first different byte.
        var diff: UInt8 = 0
        for i in 0..<expected.count { diff |= plain[i] ^ expected[i] }
        guard diff == 0 else {
            return FlutterError(code: "bad_data", message: "A sealed value did not verify", details: nil)
        }
        return FlutterStandardTypedData(bytes: plain.subdata(in: expected.count..<plain.count))
    }
}
