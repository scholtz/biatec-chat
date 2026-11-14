// Contract test: the real settings client and controller against a running
// packages/consent-protocol/mock-oidc server, which verifies the device
// signature of every loosening as Biatec OIDC would.
//
//   MOCK_JSON_GROUPS=1 MOCK_TWO_KEYS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 PORT=8089 pnpm --filter mock-oidc start   # from the repo root; 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/settings/mock_oidc_settings_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set. Run it against a fresh mock: it
// changes alice's settings (and restores them).
import 'dart:convert';

import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
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
  Future<(SettingsController, MemoryEnrollmentStore, FakeGate)> alicePhone() async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/alice-device')))
            .body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
      kid: 'alice-device',
      deviceToken: d['device_token'] as String,
      activeAt: DateTime.utc(2020),
      attestation: d['attestation'] as String,
      presenceKid: (d['presence'] as Map)['kid'] as String,
      presenceAttestation: (d['presence'] as Map)['attestation'] as String,
    ));
    final gate = FakeGate();
    final services = SettingsServices(
      client: SettingsClient(baseUrl: baseUrl!),
      keys: SoftwareKeyStore(deviceTestKey()),
      enrollment: ObservableEnrollmentStore(store),
      gate: gate,
    );
    final c = services.controller();
    await c.load();
    expect(c.phase, SettingsPhase.ready, reason: c.error);
    return (c, store, gate);
  }

  test('cancel a pending enrollment', () async {
    final (c, store, _) = await alicePhone();
    // A second phone enrolls with two keys and the enrollment proofs (the mock enforces them).
    final other = EnrollmentController(
      keys: randomSoftwareKeyStore(),
      store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
      client: OidcEnrollmentClient(baseUrl: baseUrl!),
    );
    await other.enroll(loginToken: 'login-alice');
    expect(other.state.phase, EnrollmentPhase.inactive, reason: other.state.error);
    final pendingKid = other.state.record!.kid;

    await c.load();
    final pending = c.devices.where((d) => d.pending).map((d) => d.kid);
    expect(pending, contains(pendingKid));
    await c.cancelDevice(pendingKid);
    expect(c.error, isNull);
    expect(c.devices.map((d) => d.kid), isNot(contains(pendingKid)));
    expect((await store.read())!.kid, 'alice-device');
  }, skip: skip);

  test('tighten at once, loosen with a device signature the mock verifies',
      () async {
    final (c, _, gate) = await alicePhone();
    final original = c.settings!.thresholdEur;
    expect(await c.apply({'threshold_eur': '500'}), isTrue, reason: c.error);
    // Any security change needs a fresh biometric, tightening included.
    expect(gate.asked, 1);
    expect(c.settings!.thresholdEur, '500.00');

    // Loosening: the approval key's prompt is the biometric, signed with the key the mock attested for approvals.
    expect(await c.apply({'threshold_eur': '800'}), isTrue, reason: c.error);
    expect(gate.asked, 1);
    expect(c.settings!.thresholdEur, '800.00');

    expect(await c.apply({'threshold_eur': original}), isTrue, reason: c.error);
    expect(gate.asked, 1);
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
    expect(gate.asked, 0);
    expect(c.settings!.client('bank-example')!.pausedUntil, isNull);
  }, skip: skip);
}
