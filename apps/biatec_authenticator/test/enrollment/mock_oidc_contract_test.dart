// Contract test: the real enrollment client and controller against a running
// packages/consent-protocol/mock-oidc server (no hand-written responses).
//
//   MOCK_JSON_GROUPS=1 MOCK_TWO_KEYS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 PORT=8089 pnpm --filter mock-oidc start   # from the repo root; 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/enrollment/mock_oidc_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set.

import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:flutter_test/flutter_test.dart';

import '../approvals/support.dart';
import 'package:http/http.dart' as http;

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;

  EnrollmentController install(DateTime Function() clock) =>
      EnrollmentController(
        keys: randomSoftwareKeyStore(),
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
