// Contract test: the real approval client, controller and receipt signing
// against a running packages/consent-protocol/mock-oidc server. The mock
// verifies every receipt with its JWKS, exactly as Biatec OIDC would.
//
//   pnpm --filter mock-oidc start            # listens on 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/approvals/mock_oidc_approval_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'support.dart';

class _Pass implements BiometricGate {
  @override
  Future<bool> confirm(String reason) async => true;
}

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;
  final chat =
      'Basic ${base64.encode(utf8.encode('biatec-chat:biatec-chat-secret'))}';

  /// Opens a direct-entry request for alice, as a client would.
  Future<({String id, String code})> openRequest(String fiat) async {
    final tx = base64.encode(utf8.encode(jsonEncode({
      'sender': 'ALICEADDRESS',
      'receiver': 'PAYEE',
      'asset': {'id': '31566704', 'symbol': 'USDC', 'decimals': 6},
      'amount': '$fiat.000000',
      'fiat_eur': '$fiat.00',
    })));
    final res = await http.post(Uri.parse('$baseUrl/connect/ciba'),
        headers: {'Authorization': chat, 'Content-Type': 'application/json'},
        body: jsonEncode({
          'scope': 'sign',
          'resource': 'https://api.biatec.io',
          'login_hint': 'alice@example.com',
          'network': 'algorand-testnet',
          'address': 'ALICEADDRESS',
          'unsigned_group': [tx],
          'origin': 'https://chat.biatec.io',
        }));
    expect(res.statusCode, 200, reason: res.body);
    final json = jsonDecode(res.body) as Map;
    return (
      id: json['auth_req_id'] as String,
      code: json['binding_code'] as String
    );
  }

  Future<Map<String, Object?>> token(String id) async {
    final res = await http.post(Uri.parse('$baseUrl/token'), headers: {
      'Authorization': chat
    }, body: {
      'grant_type': 'urn:openid:params:grant-type:ciba',
      'auth_req_id': id,
    });
    return {
      'status': res.statusCode,
      ...(jsonDecode(res.body) as Map).cast<String, Object?>()
    };
  }

  /// Alice's seeded device, which uses the vector device key.
  Future<ApprovalServices> alicePhone() async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/alice-device')))
            .body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
      kid: 'alice-device',
      deviceToken: d['device_token'] as String,
      activeAt: DateTime.utc(2020),
      attestation: d['attestation'] as String,
    ));
    return ApprovalServices(
      client: ApprovalClient(baseUrl: baseUrl!),
      keys: SoftwareKeyStore(deviceTestKey()),
      enrollment: store,
      gate: _Pass(),
      history: MemoryHistoryStore(),
    );
  }

  test('approve produces a receipt the mock accepts', () async {
    final r = await openRequest('2500');
    final phone = await alicePhone();
    final pending = await phone.client
        .pending((await phone.enrollment.read())!.deviceToken);
    expect(pending.map((p) => p.authReqId), contains(r.id));

    final c = phone.controllerFor(r.id);
    await c.load();
    expect(c.phase, ApprovalPhase.ready, reason: c.error);
    await c.approve(r.code);
    expect(c.phase, ApprovalPhase.approved, reason: c.error);

    final t = await token(r.id);
    expect(t['status'], 200, reason: '$t');
    final mine = (await phone.history.read()).single.receipt;
    expect(jsonEncode(t['consent_receipt']), jsonEncode(mine));
  }, skip: skip);

  test('third wrong code yields code_failed', () async {
    final r = await openRequest('1500');
    final phone = await alicePhone();
    final c = phone.controllerFor(r.id);
    await c.load();
    final wrong = r.code == '000000' ? '111111' : '000000';
    await c.approve(wrong);
    expect(c.attemptsRemaining, 2, reason: c.error);
    await c.approve(wrong);
    expect(c.attemptsRemaining, 1, reason: c.error);
    await c.approve(wrong);
    expect(c.phase, ApprovalPhase.codeFailed, reason: c.error);
    expect(c.error, isNull, reason: 'the code_failed receipt is accepted');

    final t = await token(r.id);
    expect(t['error'], 'access_denied');
    expect(t['reason'], 'code_failed');
  }, skip: skip);

  test('deny produces a signed deny receipt', () async {
    final r = await openRequest('1200');
    final phone = await alicePhone();
    final c = phone.controllerFor(r.id);
    await c.load();
    await c.deny();
    expect(c.phase, ApprovalPhase.denied, reason: c.error);
    final t = await token(r.id);
    expect(t['error'], 'access_denied');
  }, skip: skip);
}
