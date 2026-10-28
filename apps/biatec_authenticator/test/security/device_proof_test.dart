// BCHAT-030: every device call carries a proof of possession of the device key.
//
// The last test runs against a mock started in proof mode and is skipped unless MOCK_OIDC_URL is set:
//   MOCK_TEST_HELPERS=1 MOCK_DEVICE_PROOF=1 pnpm --filter mock-oidc start
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/security/device_proof_test.dart
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/device_proof.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../approvals/support.dart';

Map<String, Object?> _part(String jws, int i) => (jsonDecode(utf8.decode(
        base64Url.decode(base64Url.normalize(jws.split('.')[i])))) as Map)
    .cast<String, Object?>();

Future<KeystoreDeviceProofSigner> _signer(SoftwareKeyStore keys,
    {String? alias = 'alice-device', DateTime? now}) async {
  final enrollment = MemoryEnrollmentStore();
  if (alias != null) await enrollment.writeAlias(alias);
  return KeystoreDeviceProofSigner(
      keys: keys,
      enrollment: enrollment,
      clock: () => now ?? DateTime.utc(2026, 10, 23, 12));
}

void main() {
  final keys = SoftwareKeyStore(deviceTestKey());
  final token = 'device-token-alice';

  test(
      'the proof is an ES256 JWS bound to method, URL and token, signed by the device key',
      () async {
    final signer = await _signer(keys);
    final url = Uri.parse('https://oidc.example/devices/requests?x=1');
    final h = await signer.headers('get', url, token);
    final jws = h[deviceProofHeader]!;
    final header = _part(jws, 0);
    final payload = _part(jws, 1);
    expect(header,
        {'alg': 'ES256', 'typ': deviceProofType, 'kid': 'alice-device'});
    expect(payload['htm'], 'GET');
    expect(payload['htu'], url.toString());
    expect(payload['iat'],
        DateTime.utc(2026, 10, 23, 12).millisecondsSinceEpoch ~/ 1000);
    expect(
        payload['ath'],
        base64Url
            .encode(sha256.convert(utf8.encode(token)).bytes)
            .replaceAll('=', ''));
    expect((payload['jti'] as String).length, greaterThanOrEqualTo(16));
    final parts = jws.split('.');
    expect(
        verifyEs256(deviceTestKey(), utf8.encode('${parts[0]}.${parts[1]}'),
            base64Url.decode(base64Url.normalize(parts[2]))),
        isTrue);
    expect(jws, isNot(contains(token)),
        reason: 'the token itself is never in the proof');
  });

  test(
      'every proof has its own jti, and a missing device key is an error, never a silent bearer-only call',
      () async {
    final signer = await _signer(keys);
    final u = Uri.parse('https://oidc.example/devices');
    final a = _part(
        (await signer.headers('GET', u, token))[deviceProofHeader]!, 1)['jti'];
    final b = _part(
        (await signer.headers('GET', u, token))[deviceProofHeader]!, 1)['jti'];
    expect(a, isNot(b));
    await expectLater(
        (await _signer(keys, alias: null)).headers('GET', u, token),
        throwsStateError);
    // Through a client it is a refusal the screens already handle, and nothing is sent.
    var sent = 0;
    final client = ApprovalClient(
        baseUrl: 'https://oidc.example',
        client: MockClient((_) async {
          sent++;
          return http.Response('{}', 200);
        }),
        proof: await _signer(keys, alias: null));
    await expectLater(client.pending(token), throwsA(isA<ApprovalException>()));
    expect(sent, 0);
  });

  test(
      'the approval and settings clients send a proof with each call, and none without a signer',
      () async {
    final seen = <String, Map<String, String>>{};
    final fake = MockClient((req) async {
      seen['${req.method} ${req.url.path}'] = req.headers;
      return http.Response(
          '{"requests":[],"status":"approved","devices":[],"sub":"s","biometric_required":false,"threshold_eur":"1","block_unverified_clients":false,"clients":[]}',
          200);
    });
    final signer = await _signer(keys);
    final approvals = ApprovalClient(
        baseUrl: 'https://oidc.example', client: fake, proof: signer);
    await approvals.pending(token);
    await approvals.registerPush(token, 'fcm', 'push-token');
    final settings = SettingsClient(
        baseUrl: 'https://oidc.example', client: fake, proof: signer);
    await settings.devices(token);
    await settings.settings(token);
    expect(
        seen.keys,
        containsAll([
          'GET /devices/requests',
          'PUT /devices/push',
          'GET /devices',
          'GET /devices/settings'
        ]));
    for (final e in seen.entries) {
      expect(e.value['Authorization'], 'Device $token', reason: e.key);
      expect(e.value.containsKey(deviceProofHeader), isTrue, reason: e.key);
      final htu = _part(e.value[deviceProofHeader]!, 1);
      expect(htu['htm'], e.key.split(' ').first);
      expect(Uri.parse(htu['htu'] as String).path, e.key.split(' ').last);
    }

    seen.clear();
    await ApprovalClient(baseUrl: 'https://oidc.example', client: fake)
        .pending(token);
    expect(seen.values.single.containsKey(deviceProofHeader), isFalse);
  });

  final url = const String.fromEnvironment('MOCK_OIDC_URL');
  test(
      'against a mock in proof mode: the bearer alone is refused, the proof opens the call',
      () async {
    final info = (jsonDecode(
            (await http.get(Uri.parse('$url/mock/devices/alice-device')))
                .body) as Map)
        .cast<String, Object?>();
    final deviceToken = info['device_token'] as String;
    final plain = ApprovalClient(baseUrl: url);
    await expectLater(
        plain.pending(deviceToken), throwsA(isA<ApprovalException>()));

    final enrollment = MemoryEnrollmentStore();
    await enrollment.writeAlias(info['kid'] as String);
    final proven = ApprovalClient(
        baseUrl: url,
        proof: KeystoreDeviceProofSigner(
            keys: keys, enrollment: enrollment, clock: DateTime.now));
    expect(await proven.pending(deviceToken), isA<List>());
    expect(await proven.pending(deviceToken), isA<List>(),
        reason: 'a fresh proof each call');
  },
      skip: url.isEmpty
          ? 'set MOCK_OIDC_URL (mock in proof mode) to run'
          : false);
}
