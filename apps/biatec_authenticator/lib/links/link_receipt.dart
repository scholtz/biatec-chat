import 'dart:convert';
import 'dart:typed_data';

import '../approvals/jcs.dart';
import '../approvals/receipt.dart';
import '../keys/device_key_store.dart';
import 'link_request.dart';

/// `approve` or `deny`: the two decisions of a `biatec_link` statement.
enum LinkDecision {
  approve('approve'),
  deny('deny');

  const LinkDecision(this.wire);
  final String wire;
}

/// The `biatec_link` statement the device signs (specification 14.3 step 5, `schemas/link-signed.schema.json`): exactly nine members, built only from what the server showed ([request]),
/// the code the user typed and the key that signs. `domain` is the proven domain the screen showed, or null: signing it makes the user's decision about that domain.
Map<String, Object?> linkSignedObject(
  LinkRequest request, {
  required LinkDecision decision,
  required String code,
  required int issuedAt,
  required String kid,
}) =>
    {
      'type': 'biatec_link',
      'link_id': request.linkId,
      'client_fingerprint': request.client.fingerprint,
      'domain': request.domain,
      'decision': decision.wire,
      'binding_code': code,
      'nonce': request.nonce,
      'issued_at': issuedAt,
      'kid': kid,
    };

/// What the biometric prompt of linking says.
class LinkPrompt {
  const LinkPrompt({required this.title, required this.subtitle});

  /// The same text for every link (the operating system shows it).
  static const fixedTitle = 'Link this service';

  final String title;
  final String subtitle;
}

/// Signs JCS(statement) and returns the body of `POST /consent/links/{id}/receipt`: `{ signed, signature }`. An `approve` is signed by the APPROVAL key, which makes the operating system ask for a strong
/// biometric for this one signature ([prompt] is what it says, required); a `deny` is signed by the presence key and never prompts. `issued_at` is read from [clock] before anything is signed, because
/// it is part of what is signed. [subtitle] names the service.
Future<Map<String, Object?>> signLink({
  required DeviceKeyStore keys,
  required DeviceIdentity device,
  required LinkRequest request,
  required String code,
  required LinkDecision decision,
  required DateTime Function() clock,
  LinkPrompt? prompt,
}) async {
  final approving = decision == LinkDecision.approve;
  final key = approving ? device.approvalKey : device.presenceKey;
  if (key == null || (approving && device.approvalAttestation == null)) {
    throw StateError('this device has no approval key to sign a link');
  }
  if (approving && prompt == null) {
    throw ArgumentError('an approve statement needs the text of the prompt');
  }
  final signed = linkSignedObject(request,
      decision: decision,
      code: code,
      issuedAt: clock().toUtc().millisecondsSinceEpoch ~/ 1000,
      kid: key.kid);
  final bytes = Uint8List.fromList(canonicalBytes(signed));
  final signature = approving
      ? await keys.signApproval(bytes,
          promptTitle: prompt!.title, promptSubtitle: prompt.subtitle)
      : await keys.signPresence(bytes);
  if (signature.length != 64) {
    throw StateError('the keystore returned a ${signature.length}-byte '
        'signature; ES256 r||s is 64 bytes');
  }
  return {
    'signed': signed,
    'signature': base64Url.encode(signature).replaceAll('=', ''),
  };
}
