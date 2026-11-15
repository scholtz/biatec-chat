// T-247: what `ApprovalClient.postLinkReceipt` says when the call fails. Only a failure after the statement may have left the phone is "may or may not be recorded"; a pinning failure and a device proof
// that cannot be made are plain failures (nothing was sent).
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/security/device_proof.dart';
import 'package:biatec_authenticator/security/pinning/pin_policy.dart';
import 'package:biatec_authenticator/security/pinning/pinned_http.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

class _BrokenProof implements DeviceProofSigner {
  @override
  Future<Map<String, String>> headers(String method, Uri url, String deviceToken) async =>
      throw StateError('keystore refused');
}

Future<Object?> _post(ApprovalClient c) async {
  try {
    return await c.postLinkReceipt('t', 'lnk_6f24862262c182126431e3a6', 's', {'signed': {}, 'signature': 'x'});
  } on ApprovalException catch (e) {
    return e;
  }
}

void main() {
  test('a connection lost after sending: "may or may not be recorded", and still offline', () async {
    final c = ApprovalClient(
        baseUrl: 'http://oidc',
        client: MockClient((_) async => throw http.ClientException('connection reset')));
    final e = await _post(c) as ApprovalException;
    expect(e.message, contains('may or may not be recorded'));
    expect(e.offline, isTrue);
  });

  test('a pinning failure is a plain failure: nothing was sent, not offline', () async {
    final c = ApprovalClient(
        baseUrl: 'https://oidc.example',
        client: MockClient((_) async =>
            throw const PinningException(PinDecision.pinMismatch, 'oidc.example')));
    final e = await _post(c) as ApprovalException;
    expect(e.message, isNot(contains('may or may not')));
    expect(e.message, contains('nothing was sent'));
    expect(e.offline, isFalse);
  });

  test('a device proof that cannot be made is a plain failure before anything is sent', () async {
    var sent = 0;
    final c = ApprovalClient(
        baseUrl: 'http://oidc',
        proof: _BrokenProof(),
        client: MockClient((_) async {
          sent++;
          return http.Response('{}', 200);
        }));
    final e = await _post(c) as ApprovalException;
    expect(e.message, contains('device proof could not be made'));
    expect(e.message, isNot(contains('may or may not')));
    expect(e.offline, isFalse);
    expect(sent, 0);
  });
}
