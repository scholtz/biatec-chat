// Contract test: the real enrollment client and controller against a running
// packages/consent-protocol/mock-oidc server (no hand-written responses).
//
//   pnpm --filter mock-oidc start            # listens on 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/enrollment/mock_oidc_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set.
import 'dart:typed_data';

import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

const _fakeP256 = (
  x: 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU',
  y: 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0',
);

class _Keys implements DeviceKeyStore {
  @override
  Future<DeviceKey> generate(String alias) async => DeviceKey(
      kid: alias,
      x: _fakeP256.x,
      y: _fakeP256.y,
      securityLevel: KeySecurityLevel.trustedEnvironment);
  @override
  Future<DeviceKey?> load(String alias) async => null;
  @override
  Future<Uint8List> sign(String alias, Uint8List data) async => data;
  @override
  Future<void> delete(String alias) async {}
}

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;

  EnrollmentController install(DateTime Function() clock) =>
      EnrollmentController(
        keys: _Keys(),
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: baseUrl!),
        clock: clock,
      );

  test('two installs enroll, activate and receive a recovery code once',
      () async {
    var now = DateTime.now().toUtc();
    final a = install(() => now);
    final b = install(() => now);

    await a.enroll(loginToken: 'login-alice');
    await b.enroll(loginToken: 'login-bob');
    expect(a.state.phase, EnrollmentPhase.inactive, reason: a.state.error);
    expect(b.state.phase, EnrollmentPhase.inactive, reason: b.state.error);
    expect(a.state.record!.kid, isNot(b.state.record!.kid));

    // Inactive: the server refuses a recovery code.
    final early = await http.post(
      Uri.parse('$baseUrl/devices/recovery-code'),
      headers: {'Authorization': 'Device ${a.state.record!.deviceToken}'},
    );
    expect(early.statusCode, 403);

    // Skip the 24 h delay on the server and in the controller's clock.
    await http.post(
        Uri.parse('$baseUrl/mock/devices/${a.state.record!.kid}/activate'));
    now = now.add(const Duration(hours: 25));
    await a.refresh();
    expect(a.state.phase, EnrollmentPhase.activeRecoveryPending);
    expect(a.recoveryCodeToShow,
        matches(RegExp(r'^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$')));

    await a.acknowledgeRecoveryCode();
    expect(a.state.phase, EnrollmentPhase.active);
    expect(a.recoveryCodeToShow, isNull);
  }, skip: baseUrl == null ? 'set MOCK_OIDC_URL to run' : false);
}
