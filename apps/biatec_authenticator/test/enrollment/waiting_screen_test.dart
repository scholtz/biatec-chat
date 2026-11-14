// T-242: a new device that waits for another device shows the pairing code, the 24 hour text and "Cancel this device", and asks Biatec OIDC every 5 seconds
// whether it was activated.
import 'dart:convert';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../keys/fake_two_keys.dart';
import 'in_app_login_test.dart' show FakeAuth;

void main() {
  late List<http.Request> requests;
  late bool active;
  late MemoryEnrollmentStore disk;
  late EnrollmentController controller;
  late FakeTwoKeyStore keys;

  setUp(() async {
    requests = [];
    active = false;
    keys = FakeTwoKeyStore();
    disk = MemoryEnrollmentStore();
    await disk.write(EnrollmentRecord(
      kid: 'dev-new',
      deviceToken: 'tok',
      activeAt: DateTime.now().toUtc().add(const Duration(hours: 24)),
      attestation: 'a.b.c',
      presenceKid: 'dev-new-presence',
      presenceAttestation: 'p.b.c',
      pairingCode: '102716',
    ));
    final client = MockClient((r) async {
      requests.add(r);
      if (r.method == 'GET' && r.url.path == '/devices') {
        return http.Response(
            jsonEncode({
              'devices': [
                {
                  'kid': 'dev-new',
                  'current': true,
                  'active': active,
                  'active_at': DateTime.now().millisecondsSinceEpoch ~/ 1000 +
                      (active ? -1 : 86400),
                }
              ]
            }),
            200);
      }
      if (r.url.path.endsWith('/cancel')) {
        return http.Response(jsonEncode({'cancelled': true}), 200);
      }
      return http.Response(jsonEncode({'recovery_code': 'AAAA-BBBB-CCCC'}), 200);
    });
    controller = EnrollmentController(
      keys: keys,
      store: ObservableEnrollmentStore(disk),
      client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
      twoKeys: true,
      auth: FakeAuth([]),
      loginRequest: AuthRequest.forConfig(AppConfig.resolve('')),
      devices: SettingsClient(baseUrl: 'http://oidc', client: client),
    );
  });

  Future<void> open(WidgetTester tester) async {
    tester.view.physicalSize = const Size(400, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(
        theme: buildTheme(Brightness.light),
        home: EnrollmentScreen(
            config: AppConfig.resolve(''), controller: controller)));
    await tester.pump();
    await tester.pump();
  }

  testWidgets('shows the pairing code, what to do with it and the 24 hour text',
      (tester) async {
    await open(tester);
    expect(controller.state.phase, EnrollmentPhase.inactive);
    expect(find.byKey(const Key('pairing-code')), findsOneWidget);
    expect(find.text('102 716'), findsOneWidget);
    expect(find.byKey(const Key('pairing-help')), findsOneWidget);
    expect(find.textContaining('email with a link to cancel it'),
        findsOneWidget);
    expect(find.byKey(const Key('cancel-this-device')), findsOneWidget);
  });

  testWidgets('asks Biatec OIDC every 5 seconds and opens when another device activated it',
      (tester) async {
    await open(tester);
    expect(requests.where((r) => r.url.path == '/devices'), isEmpty);
    await tester.pump(const Duration(seconds: 5));
    expect(requests.where((r) => r.url.path == '/devices'), hasLength(1));
    expect(controller.state.phase, EnrollmentPhase.inactive);
    await tester.pump(const Duration(seconds: 5));
    expect(requests.where((r) => r.url.path == '/devices'), hasLength(2));
    active = true;
    await tester.pump(const Duration(seconds: 5));
    await tester.pump();
    expect(controller.state.phase, EnrollmentPhase.activeRecoveryPending,
        reason: controller.state.error);
    // The screen is left alone from then on (the app shows the recovery code).
    await tester.pumpAndSettle(const Duration(seconds: 1));
  });

  testWidgets('"Cancel this device" asks first, then cancels and starts over',
      (tester) async {
    await open(tester);
    await tester.ensureVisible(find.byKey(const Key('cancel-this-device')));
    await tester.tap(find.byKey(const Key('cancel-this-device')));
    await tester.pumpAndSettle();
    expect(find.text('Cancel this device?'), findsOneWidget);
    await tester.tap(find.byKey(const Key('keep-this-device')));
    await tester.pumpAndSettle();
    expect(requests.where((r) => r.url.path.endsWith('/cancel')), isEmpty);

    await tester.tap(find.byKey(const Key('cancel-this-device')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('confirm-cancel-this-device')));
    await tester.pumpAndSettle();
    final cancel = requests.singleWhere((r) => r.url.path.endsWith('/cancel'));
    expect(cancel.url.path, '/devices/dev-new/cancel');
    expect(controller.state.phase, EnrollmentPhase.notEnrolled);
    expect(await disk.read(), isNull);
  });
}
