import Flutter
import Foundation
import Security

/// Secure Enclave implementation of the device key store channel.
/// The private key is created with kSecAttrTokenIDSecureEnclave: it cannot be exported, only used to sign.
/// Secure Enclave is absent on the simulator; there, generate fails with `no_secure_enclave`.
public class KeystorePlugin: NSObject, FlutterPlugin {
    public static func register(with registrar: FlutterPluginRegistrar) {
        let channel = FlutterMethodChannel(name: "io.biatec.authenticator/keystore", binaryMessenger: registrar.messenger())
        registrar.addMethodCallDelegate(KeystorePlugin(), channel: channel)
    }

    public func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        guard let args = call.arguments as? [String: Any], let alias = args["alias"] as? String, !alias.isEmpty else {
            return result(FlutterError(code: "bad_args", message: "alias is required", details: nil))
        }
        // Secure Enclave key generation and signing can block for a while: off the main thread, with the result posted back to it.
        let method = call.method
        DispatchQueue.global(qos: .userInitiated).async {
            self.run(method, args, alias) { value in DispatchQueue.main.async { result(value) } }
        }
    }

    private func run(_ method: String, _ args: [String: Any], _ alias: String, _ result: (Any?) -> Void) {
        switch method {
        case "generate":
            if let key = privateKey(alias) { return result(keyParts(key)) }
            generate(alias, result)
        case "load":
            result(privateKey(alias).map { keyParts($0) })
        case "sign":
            guard let data = (args["data"] as? FlutterStandardTypedData)?.data, let key = privateKey(alias) else {
                return result(FlutterError(code: "no_key", message: "No key or data", details: nil))
            }
            var err: Unmanaged<CFError>?
            guard let der = SecKeyCreateSignature(key, .ecdsaSignatureMessageX962SHA256, data as CFData, &err) as Data? else {
                return result(FlutterError(code: "keystore_error", message: err?.takeRetainedValue().localizedDescription, details: nil))
            }
            result(FlutterStandardTypedData(bytes: derToRaw(der)))
        case "delete":
            SecItemDelete(query(alias) as CFDictionary)
            result(nil)
        default:
            result(FlutterMethodNotImplemented)
        }
    }

    private func tag(_ alias: String) -> Data { Data(alias.utf8) }

    private func query(_ alias: String) -> [String: Any] {
        [kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag(alias), kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom]
    }

    private func privateKey(_ alias: String) -> SecKey? {
        var q = query(alias)
        q[kSecReturnRef as String] = true
        var item: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess ? (item as! SecKey) : nil
    }

    private func generate(_ alias: String, _ result: FlutterResult) {
        var err: Unmanaged<CFError>?
        guard let access = SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, .privateKeyUsage, &err) else {
            return result(FlutterError(code: "keystore_error", message: "access control", details: nil))
        }
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecAttrTokenID as String: kSecAttrTokenIDSecureEnclave,
            kSecPrivateKeyAttrs as String: [kSecAttrIsPermanent as String: true, kSecAttrApplicationTag as String: tag(alias), kSecAttrAccessControl as String: access],
        ]
        guard let key = SecKeyCreateRandomKey(attrs as CFDictionary, &err) else {
            return result(FlutterError(code: "no_secure_enclave", message: err?.takeRetainedValue().localizedDescription, details: nil))
        }
        result(keyParts(key))
    }

    /// The public key as the channel returns it, or an error that says what is wrong instead of a nil that Dart reads as malformed data.
    private func keyParts(_ key: SecKey) -> Any {
        publicParts(key) ?? FlutterError(code: "keystore_error", message: "The public key has an unexpected shape", details: nil)
    }

    private func b64(_ d: Data) -> String {
        d.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    private func publicParts(_ key: SecKey) -> [String: String]? {
        guard let pub = SecKeyCopyPublicKey(key), let raw = SecKeyCopyExternalRepresentation(pub, nil) as Data?, raw.count == 65, raw[0] == 4 else { return nil }
        return ["x": b64(raw.subdata(in: 1..<33)), "y": b64(raw.subdata(in: 33..<65)), "securityLevel": securityLevel(key)]
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
