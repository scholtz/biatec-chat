// Contract test: push registration against a running
// packages/consent-protocol/mock-oidc server, and the payload the mock would
// hand to FCM for a new request.
//
//   pnpm --filter mock-oidc start            # listens on 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/push/mock_oidc_push_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set.
import 'dart:convert';

import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/push/push_controller.dart';
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'push_test.dart' show FakeTransport;

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;

  test('registered device receives a push with only the request id', () async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/alice-device')))
            .body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
        kid: 'alice-device',
        deviceToken: d['device_token'] as String,
        activeAt: DateTime.utc(2020)));
    final transport = FakeTransport()..current = 'fcm-contract-token';
    final opened = <String>[];
    final push = PushController(
      enrollment: store,
      client: ApprovalClient(baseUrl: baseUrl!),
      onOpenRequest: opened.add,
    );
    await push.start(transport);

    final tx = base64.encode(utf8.encode(jsonEncode({
      'sender': 'ALICEADDRESS',
      'receiver': 'PAYEE',
      'asset': {'id': '31566704', 'symbol': 'USDC', 'decimals': 6},
      'amount': '5000.000000',
      'fiat_eur': '5000.00',
    })));
    final res = await http.post(Uri.parse('$baseUrl/connect/ciba'),
        headers: {
          'Authorization':
              'Basic ${base64.encode(utf8.encode('biatec-chat:biatec-chat-secret'))}',
          'Content-Type': 'application/json',
        },
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
    final id = (jsonDecode(res.body) as Map)['auth_req_id'];

    final outbox = jsonDecode((await http
            .get(Uri.parse('$baseUrl/mock/push-outbox?kid=alice-device')))
        .body) as Map;
    final last = (outbox['messages'] as List).last as Map;
    expect(last['token'], 'fcm-contract-token');
    expect(last['data'], {'auth_req_id': id});

    // What FCM delivers on tap is that data; the controller opens the request.
    transport.openedC.add((last['data'] as Map).cast<String, Object?>());
    await pumpEventQueue();
    expect(opened, [id]);
    await push.dispose();
  }, skip: skip);
}
