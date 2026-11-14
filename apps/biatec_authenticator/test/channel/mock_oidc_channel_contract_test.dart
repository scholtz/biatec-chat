// Contract test: the device channel against a running packages/consent-protocol/mock-oidc server. A request opened in the mock arrives over the
// channel, with a ticket fetched from `POST /devices/hub-ticket`.
//
//   MOCK_JSON_GROUPS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 PORT=8089 pnpm --filter mock-oidc start   # from the repo root; 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/channel/mock_oidc_channel_contract_test.dart
//
// It needs the mock's device hub (T-244): `POST /devices/hub-ticket` answering `{ticket, expires_in}` and a WebSocket at `/hubs/device?access_token=<ticket>` that
// sends `consent_request {auth_req_id}`, `consent_closed {auth_req_id}`, `device_enrolled {kid}` and `settings_changed {}`. The mock's own state
// (`/mock/devices/alice-device`, `POST /connect/ciba`) is used as in the other contract tests. Skipped when MOCK_OIDC_URL is not set.
import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/channel/device_channel.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/push/device_event.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  // The test binding blocks real HTTP; the mock is on localhost.
  HttpOverrides.global = null;
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;

  test('a request opened in the mock arrives over the channel', () async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/alice-device')))
            .body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
        kid: 'alice-device',
        deviceToken: d['device_token'] as String,
        activeAt: DateTime.utc(2020)));
    final client = ApprovalClient(baseUrl: baseUrl!);
    var tickets = 0;
    final channel = DeviceChannel(
      hubUri: DeviceChannel.hubUriFor(baseUrl),
      enrolled: () async => (await store.read()) != null,
      ticket: () async {
        tickets++;
        final record = (await store.read())!;
        return (await client.hubTicket(record.deviceToken)).ticket;
      },
    );
    final arrived = Completer<ConsentRequested>();
    final sub = channel.events.listen((e) {
      if (e is ConsentRequested && !arrived.isCompleted) arrived.complete(e);
    });
    await channel.start();
    // Give the handshake a moment: the mock sends events to connections that are open.
    await Future<void>.delayed(const Duration(milliseconds: 500));

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

    final event = await arrived.future.timeout(const Duration(seconds: 10));
    expect(event.authReqId, id);
    expect(tickets, greaterThanOrEqualTo(1));
    await sub.cancel();
    await channel.dispose();
  }, skip: skip);
}
