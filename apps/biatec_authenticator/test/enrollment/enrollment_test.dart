import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

class FakeKeyStore implements DeviceKeyStore {
  bool failGenerate = false;
  final List<String> deleted = [];
  DeviceKey? key;

  @override
  Future<DeviceKey> generate(String alias) async {
    if (failGenerate) throw Exception('no secure hardware');
    return key ??= DeviceKey(kid: alias, x: 'xx', y: 'yy');
  }

  @override
  Future<DeviceKey?> load(String alias) async => key;

  @override
  Future<Uint8List> sign(String alias, Uint8List data) async => data;

  @override
  Future<void> delete(String alias) async {
    deleted.add(alias);
    key = null;
  }
}

void main() {
  final t0 = DateTime.utc(2026, 10, 19, 9);
  late DateTime clock;
  late FakeKeyStore keys;
  late MemoryEnrollmentStore store;
  late List<http.Request> requests;

  EnrollmentController build(http.Response Function(http.Request) respond) {
    final client = MockClient((r) async {
      requests.add(r);
      return respond(r);
    });
    return EnrollmentController(
      keys: keys,
      store: store,
      client: OidcEnrollmentClient(baseUrl: 'http://mock', client: client),
      clock: () => clock,
    );
  }

  http.Response created({int? activeAt, String? recovery = 'AAAA-BBBB-CCCC'}) =>
      http.Response(
        jsonEncode({
          'kid': EnrollmentController.keyAlias,
          'active': false,
          'active_at':
              activeAt ?? t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/ 1000,
          'device_token': 'tok',
          if (recovery != null) 'recovery_code': recovery,
        }),
        201,
      );

  setUp(() {
    clock = t0;
    keys = FakeKeyStore();
    store = MemoryEnrollmentStore();
    requests = [];
  });

  group('state machine', () {
    test('starts not enrolled', () {
      expect(build((_) => created()).state.phase, EnrollmentPhase.notEnrolled);
    });

    test('enrollment sends the public key only and becomes inactive', () async {
      final c = build((_) => created());
      final phases = <EnrollmentPhase>[];
      c.addListener(() => phases.add(c.state.phase));
      await c.enroll(loginToken: 'login-alice');

      expect(phases, [
        EnrollmentPhase.generatingKey,
        EnrollmentPhase.enrolling,
        EnrollmentPhase.inactive,
      ]);
      final req = requests.single;
      expect(req.url.toString(), 'http://mock/devices');
      expect(req.headers['Authorization'], 'Bearer login-alice');
      final body = jsonDecode(req.body) as Map<String, Object?>;
      expect(body['public_key'],
          {'kty': 'EC', 'crv': 'P-256', 'x': 'xx', 'y': 'yy'});
      expect(body.containsKey('d'), isFalse);
      expect(c.state.remaining(clock), const Duration(hours: 24));
    });

    test('inactive turns active once the delay has passed', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      clock = t0.add(const Duration(hours: 23, minutes: 59));
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.inactive);
      expect(c.recoveryCodeToShow, isNull);

      clock = t0.add(const Duration(hours: 24));
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.activeRecoveryPending);
      expect(c.recoveryCodeToShow, 'AAAA-BBBB-CCCC');
    });

    test('approved by an active device is active at once', () async {
      final c = build((_) => created(
          activeAt: t0.millisecondsSinceEpoch ~/ 1000, recovery: null));
      await c.enroll(loginToken: 'login-alice', approvedByKid: 'alice-device');
      expect(c.state.phase, EnrollmentPhase.active);
      expect(
          (jsonDecode(requests.single.body) as Map)['approved_by_kid'],
          'alice-device');
    });

    test('recovery code is shown once, then erased', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      clock = t0.add(const Duration(hours: 25));
      await c.refresh();
      await c.acknowledgeRecoveryCode();
      expect(c.state.phase, EnrollmentPhase.active);
      expect(c.recoveryCodeToShow, isNull);

      // A restart reads the persisted record and does not show it again.
      final fresh = build((_) => created());
      await fresh.refresh();
      expect(fresh.state.phase, EnrollmentPhase.active);
      expect(fresh.recoveryCodeToShow, isNull);
      expect((await store.read())!.recoveryCode, isNull);
    });

    test('an enrollment state survives a restart', () async {
      await build((_) => created()).enroll(loginToken: 'login-alice');
      final fresh = build((_) => created());
      await fresh.refresh();
      expect(fresh.state.phase, EnrollmentPhase.inactive);
    });

    test('server refusal fails, removes the key and allows retry', () async {
      var attempt = 0;
      final c = build((_) => ++attempt == 1
          ? http.Response('{"error":"access_denied"}', 403)
          : created());
      await c.enroll(loginToken: 'bad');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(c.state.error, contains('403'));
      expect(keys.deleted, [EnrollmentController.keyAlias]);

      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.inactive);
    });

    test('key generation failure fails without calling the server', () async {
      keys.failGenerate = true;
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(requests, isEmpty);
    });

    test('empty login token is refused locally', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: '  ');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(requests, isEmpty);
    });

    test('enrolling twice is ignored', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      await c.enroll(loginToken: 'login-alice');
      expect(requests, hasLength(1));
    });
  });

  group('screen', () {
    Future<EnrollmentController> pump(WidgetTester tester) async {
      final c = build((_) => created());
      await tester.pumpWidget(MaterialApp(
        home: EnrollmentScreen(config: AppConfig.resolve(''), controller: c),
      ));
      return c;
    }

    testWidgets('shows the inactive state during the delay', (tester) async {
      final c = await pump(tester);
      await tester.runAsync(() => c.enroll(loginToken: 'login-alice'));
      await tester.pump();
      expect(find.text('Device inactive'), findsOneWidget);
      expect(find.textContaining('24:00:00'), findsOneWidget);
      expect(find.text('Recovery code'), findsNothing);
      // Dispose the screen so its ticker is cancelled.
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('shows the recovery code after activation', (tester) async {
      final c = await pump(tester);
      await tester.runAsync(() async {
        await c.enroll(loginToken: 'login-alice');
        clock = t0.add(const Duration(hours: 24));
        await c.refresh();
      });
      await tester.pump();
      expect(find.text('Recovery code'), findsOneWidget);
      expect(find.text('AAAA-BBBB-CCCC'), findsOneWidget);

      await tester.runAsync(() async {
        await tester.tap(find.text('I have saved it'));
      });
      await tester.pump();
      expect(find.text('AAAA-BBBB-CCCC'), findsNothing);
      expect(find.text('This device is enrolled and active.'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
