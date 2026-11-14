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
      // 1.1 names the client by fingerprint; 1.0 by its entity id.
      if (request.client.legacy)
        'client_entity_id': request.client.entityId
      else
        'client_fingerprint': request.client.fingerprint,
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

/// What the device needs to sign a receipt: both public keys with the attestation Biatec OIDC issued for each (protocol 1.1: each key has its own, naming its use).
class DeviceIdentity {
  const DeviceIdentity({
    this.approvalKey,
    this.approvalAttestation,
    required this.presenceKey,
    required this.presenceAttestation,
  });

  /// The approval key (bound to the biometric check) and its attestation (`biatec_key_use: approve`). Null when the approval key is invalidated or gone: such a device can still deny.
  final DeviceKey? approvalKey;
  final String? approvalAttestation;

  /// The presence key and its attestation (`biatec_key_use: presence`).
  final DeviceKey presenceKey;
  final String presenceAttestation;
}

/// What the biometric prompt of an approval says: the app's fixed title, and a subtitle that names the client and the total.
class ApprovalPrompt {
  const ApprovalPrompt({required this.title, required this.subtitle});

  /// The same text for every approval (the operating system shows it; on iOS it is the whole reason line).
  static const fixedTitle = 'Approve this action';

  final String title;
  final String subtitle;
}

/// Signs JCS([signedObject]) and wraps it in the receipt envelope (`schemas/consent-receipt.schema.json`).
///
/// An `approve` receipt is signed by the approval key, which makes the operating system ask for a strong biometric for this one signature
/// ([prompt] is what it says; it is required for an approve). `deny` and `code_failed` are signed by the presence key and never prompt.
///
/// A wrong or empty code is still signed as typed: Biatec OIDC compares it, and a deny or code_failed receipt carries whatever was last typed.
Future<Map<String, Object?>> signReceipt({
  required DeviceKeyStore keys,
  required DeviceIdentity device,
  required ConsentRequest request,
  required String code,
  required Decision decision,
  required DateTime now,
  ApprovalPrompt? prompt,
  DateTime Function()? clock,
  bool singleKey = false,
}) async {
  final signed = signedObject(request, code: code, decision: decision);
  final bytes = Uint8List.fromList(canonicalBytes(signed));
  final approving = decision == Decision.approve && !singleKey;
  // Before anything is signed, so the user is never asked for a biometric that cannot lead to a receipt.
  if (approving &&
      (device.approvalKey == null || device.approvalAttestation == null)) {
    throw StateError('this device has no approval key to sign an approval');
  }
  final Uint8List signature;
  if (approving) {
    if (prompt == null) {
      throw ArgumentError('an approve receipt needs the text of the prompt');
    }
    signature = await keys.signApproval(bytes,
        promptTitle: prompt.title, promptSubtitle: prompt.subtitle);
  } else {
    signature = await keys.signPresence(bytes);
  }
  if (signature.length != 64) {
    throw StateError('the keystore returned a ${signature.length}-byte '
        'signature; ES256 r||s is 64 bytes');
  }
  final key = approving ? device.approvalKey : device.presenceKey;
  // The time is read after the signature (an approval waits for the user at the prompt), so `issued_at` is when it was made.
  final issued = (clock?.call() ?? now).toUtc().millisecondsSinceEpoch ~/ 1000;
  final attestation =
      approving ? device.approvalAttestation : device.presenceAttestation;
  if (key == null || attestation == null) {
    throw StateError('this device has no approval key to sign an approval');
  }
  return {
    'version': request.client.legacy ? '1' : '1.1',
    'signed': signed,
    'alg': 'ES256',
    'signature': base64Url.encode(signature).replaceAll('=', ''),
    'issued_at': issued,
    'device': {
      'kid': key.kid,
      // 1.1: informational copy of the attestation's biatec_key_use.
      // Single-key mode: the one key is the pre-two-keys device key, which the server knows as the approval key.
      if (!request.client.legacy)
        'key_use': approving || singleKey ? 'approve' : 'presence',
      'public_key': key.toJwk(),
      'attestation': attestation,
    },
  };
}
