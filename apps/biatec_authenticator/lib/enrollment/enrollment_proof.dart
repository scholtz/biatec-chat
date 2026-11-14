import 'dart:convert';
import 'dart:typed_data';

import '../keys/device_key_store.dart';

/// `typ` of the proof signed by the approval key (protocol 1.1, section 8.3).
const enrollProofType = 'biatec-device-enroll+jwt';

/// `typ` of the second proof, over the same payload, signed by the presence key.
const enrollPresenceProofType = 'biatec-device-enroll-presence+jwt';

/// The two compact JWS a new device sends in `POST /devices`.
class EnrollmentProofs {
  const EnrollmentProofs({required this.approvalProof, required this.presenceProof});

  final String approvalProof;
  final String presenceProof;
}

String _b64(List<int> bytes) => base64Url.encode(bytes).replaceAll('=', '');

/// Builds the enrollment proof (`schemas/device-enroll-proof.schema.json`): a JWS signed by the new APPROVAL key (which carries it as the header `jwk`) over `{kid, presence_kid, presence_jwk_thumbprint, nonce, iat}`, and a `presence_proof` over the SAME payload signed by the presence key, so a key named only by its thumbprint has proven possession too.
///
/// The approval signature is the one prompt of an enrollment: the operating system asks for a strong biometric ([promptTitle], [promptSubtitle]).
Future<EnrollmentProofs> buildEnrollmentProofs({
  required DeviceKeyStore keys,
  required DeviceKeys deviceKeys,
  required String nonce,
  required DateTime now,
  required String promptTitle,
  required String promptSubtitle,
}) async {
  final payload = _b64(utf8.encode(jsonEncode({
    'kid': deviceKeys.approval.kid,
    'presence_kid': deviceKeys.presence.kid,
    'presence_jwk_thumbprint': deviceKeys.presence.thumbprint,
    'nonce': nonce,
    'iat': now.toUtc().millisecondsSinceEpoch ~/ 1000,
  })));
  String input(String typ, DeviceKey key) => '${_b64(utf8.encode(jsonEncode({
        'alg': 'ES256',
        'typ': typ,
        'jwk': key.toJwk(),
      })))}.$payload';

  final approvalInput = input(enrollProofType, deviceKeys.approval);
  final approvalSig = await keys.signApproval(
      Uint8List.fromList(utf8.encode(approvalInput)),
      promptTitle: promptTitle,
      promptSubtitle: promptSubtitle);
  final presenceInput = input(enrollPresenceProofType, deviceKeys.presence);
  final presenceSig =
      await keys.signPresence(Uint8List.fromList(utf8.encode(presenceInput)));
  if (approvalSig.length != 64 || presenceSig.length != 64) {
    throw StateError('the keystore returned a signature that is not r||s of 64 bytes');
  }
  return EnrollmentProofs(
    approvalProof: '$approvalInput.${_b64(approvalSig)}',
    presenceProof: '$presenceInput.${_b64(presenceSig)}',
  );
}
