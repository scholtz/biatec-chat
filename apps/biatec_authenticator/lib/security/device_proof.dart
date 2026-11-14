import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';

/// Header that carries the proof of possession of the device key.
const deviceProofHeader = 'X-Device-Proof';

/// `typ` of the proof JWS.
const deviceProofType = 'biatec-device-proof+jwt';

/// Adds a proof of possession of the device key to a device call (BCHAT-030).
///
/// `Authorization: Device <token>` is a long-lived bearer: whoever copies it can list the user's requests and re-point the push target.
/// With a proof every call also carries a short-lived ES256 JWS signed by the device key, bound to the method, the URL and the token, and
/// used once (the DPoP idea, RFC 9449). Without the key the token is no longer enough.
abstract class DeviceProofSigner {
  /// The proof header for one call. Throws when it cannot be made (no device key, the keystore refused).
  Future<Map<String, String>> headers(String method, Uri url, String deviceToken);
}

/// Signs the proof with the presence key in the keystore (protocol 1.1): it needs no prompt, which a proof on every HTTP call could not have. The approval key never signs a proof.
class KeystoreDeviceProofSigner implements DeviceProofSigner {
  KeystoreDeviceProofSigner({
    required this.keys,
    required this.enrollment,
    DateTime Function()? clock,
    Random? random,
  })  : _clock = clock ?? DateTime.now,
        _random = random ?? Random.secure();

  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final DateTime Function() _clock;
  final Random _random;
  final Map<String, String> _kids = {};

  static String _b64(List<int> bytes) => base64Url.encode(bytes).replaceAll('=', '');

  @override
  Future<Map<String, String>> headers(String method, Uri url, String deviceToken) async {
    // The kid is cached by device token (a new enrollment has a new token), so the record is not decoded on every call.
    var presenceKid = _kids[deviceToken];
    if (presenceKid == null) {
      final record = await enrollment.read();
      // Two-key record: the presence key's kid. Single-key record: the one enrolled key is the prompt-free key, under the record's kid.
      presenceKid = record?.presenceKid ?? record?.kid;
      if (presenceKid != null && record?.deviceToken == deviceToken) {
        _kids[deviceToken] = presenceKid;
      }
    }
    // No presence key means no device key to prove with (a new install, or one from before the two keys): a configured signer never silently falls back to the bearer token alone.
    if (presenceKid == null) throw StateError('this install has no device key to prove with');
    final header = {'alg': 'ES256', 'typ': deviceProofType, 'kid': presenceKid};
    final payload = {
      'htm': method.toUpperCase(),
      'htu': url.toString(),
      'iat': _clock().toUtc().millisecondsSinceEpoch ~/ 1000,
      'jti': _b64(List<int>.generate(16, (_) => _random.nextInt(256))),
      'ath': _b64(sha256.convert(utf8.encode(deviceToken)).bytes),
    };
    final input = '${_b64(utf8.encode(jsonEncode(header)))}.${_b64(utf8.encode(jsonEncode(payload)))}';
    final signature = await keys.signPresence(Uint8List.fromList(utf8.encode(input)));
    if (signature.length != 64) {
      throw StateError('the keystore returned a ${signature.length}-byte signature, expected r||s of 64');
    }
    return {deviceProofHeader: '$input.${_b64(signature)}'};
  }
}
