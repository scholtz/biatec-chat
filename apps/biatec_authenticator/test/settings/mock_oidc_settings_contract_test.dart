// Contract test: the real settings client and controller against a running
// packages/consent-protocol/mock-oidc server, which verifies the device
// signature of every loosening as Biatec OIDC would.
//
//   pnpm --filter mock-oidc start            # listens on 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/settings/mock_oidc_settings_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set. Run it against a fresh mock: it
// changes alice's settings (and restores them).
import 'dart:convert';

import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../approvals/support.dart';
import 'support.dart';

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;

  /// Alice's seeded device, which signs with the vector device key.
  Future<(SettingsController, MemoryEnrollmentStore, FakeGate)>
      alicePhone() async {
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
    final gate = FakeGate();
    final services = SettingsServices(
      client: SettingsClient(baseUrl: baseUrl!),
      keys: SoftwareKeyStore(deviceTestKey()),
      enrollment: store,
      gate: gate,
    );
    final c = services.controller();
    await c.load();
    expect(c.phase, SettingsPhase.ready, reason: c.error);
    return (c, store, gate);
  }

  test('cancel a pending enrollment', () async {
    final (c, store, _) = await alicePhone();
    final enroll = await http.post(Uri.parse('$baseUrl/devices'),
        headers: {
          'Authorization': 'Bearer login-alice',
          'Content-Type': 'application/json'
        },
        body: jsonEncode({
          'kid': 'alice-flutter-pending',
          'public_key': {
            'kty': 'EC',
            'crv': 'P-256',
            'x': deviceTestKey()['x'],
            'y': deviceTestKey()['y'],
          },
        }));
    expect(enroll.statusCode, 201, reason: enroll.body);

    await c.load();
    final pending = c.devices.where((d) => d.pending).map((d) => d.kid);
    expect(pending, contains('alice-flutter-pending'));
    await c.cancelDevice('alice-flutter-pending');
    expect(c.error, isNull);
    expect(
        c.devices.map((d) => d.kid), isNot(contains('alice-flutter-pending')));
    expect((await store.read())!.kid, 'alice-device');
  }, skip: skip);

  test('tighten at once, loosen with a device signature the mock verifies',
      () async {
    final (c, _, gate) = await alicePhone();
    final original = c.settings!.thresholdEur;
    expect(await c.apply({'threshold_eur': '500'}), isTrue, reason: c.error);
    expect(gate.asked, 0);
    expect(c.settings!.thresholdEur, '500.00');

    expect(await c.apply({'threshold_eur': '800'}), isTrue, reason: c.error);
    expect(gate.asked, 1);
    expect(c.settings!.thresholdEur, '800.00');

    expect(await c.apply({'threshold_eur': original}), isTrue, reason: c.error);
    expect(c.settings!.thresholdEur, original);
  }, skip: skip);

  test('a client pause is lifted with a signed consent', () async {
    final (c, _, gate) = await alicePhone();
    final paused = await http.post(
        Uri.parse('$baseUrl/mock/users/pw_alice/clients/bank-example/pause'));
    expect(paused.statusCode, 200, reason: paused.body);
    await c.load();
    expect(c.settings!.client('bank-example')!.pausedUntil, isNotNull);

    expect(
        await c.apply({
          'clients': {
            'bank-example': {'paused': false}
          }
        }),
        isTrue,
        reason: c.error);
    expect(gate.asked, 1);
    expect(c.settings!.client('bank-example')!.pausedUntil, isNull);
  }, skip: skip);
}
