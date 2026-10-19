import 'dart:convert';
import 'dart:typed_data';

import '../keys/device_key_store.dart';
import 'consent_request.dart';
import 'jcs.dart';

/// `approve`, `deny` or `code_failed` (spec section 8).
enum Decision {
  approve('approve'),
  deny('deny'),
  codeFailed('code_failed');

  const Decision(this.wire);
  final String wire;

  static Decision? fromWire(Object? s) {
    for (final d in values) {
      if (d.wire == s) return d;
    }
    return null;
  }
}

/// The member set the device signs, built only from the fetched [request] and
/// the [code] the user typed.
Map<String, Object?> signedObject(
  ConsentRequest request, {
  required String code,
  required Decision decision,
}) =>
    {
      'authorization_details': request.authorizationDetails,
      'auth_req_id': request.authReqId,
      'client_entity_id': request.client.entityId,
      'binding_code': code,
      'decision': decision.wire,
      'expiry': request.expiry,
      'sub': request.sub,
      // Only what was rendered is signed (rule 3): nothing for unverified
      // clients.
      'binding_message': renderedBindingMessage(request),
    };

/// The binding message as the card shows it.
String renderedBindingMessage(ConsentRequest request) =>
    request.client.verified ? request.bindingMessage : '';

/// What the device needs to sign a receipt.
class DeviceIdentity {
  const DeviceIdentity({
    required this.key,
    required this.attestation,
  });

  /// The enrolled hardware key; [DeviceKey.kid] is also the keystore alias.
  final DeviceKey key;

  /// Biatec OIDC's attestation JWS for this key.
  final String attestation;
}

/// Signs JCS([signedObject]) with the device key and wraps it in the receipt
/// envelope (`schemas/consent-receipt.schema.json`).
///
/// A wrong or empty code is still signed as typed: Biatec OIDC compares it,
/// and a deny or code_failed receipt carries whatever was last typed.
Future<Map<String, Object?>> signReceipt({
  required DeviceKeyStore keys,
  required DeviceIdentity device,
  required ConsentRequest request,
  required String code,
  required Decision decision,
  required DateTime now,
}) async {
  final signed = signedObject(request, code: code, decision: decision);
  final signature = await keys.sign(
      device.key.kid, Uint8List.fromList(canonicalBytes(signed)));
  if (signature.length != 64) {
    throw StateError('the keystore returned a ${signature.length}-byte '
        'signature; ES256 r||s is 64 bytes');
  }
  return {
    'version': '1',
    'signed': signed,
    'alg': 'ES256',
    'signature': base64Url.encode(signature).replaceAll('=', ''),
    'issued_at': now.toUtc().millisecondsSinceEpoch ~/ 1000,
    'device': {
      'kid': device.key.kid,
      'public_key': device.key.toJwk(),
      'attestation': device.attestation,
    },
  };
}
