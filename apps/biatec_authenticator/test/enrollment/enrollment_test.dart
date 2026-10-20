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
  bool throwTypeError = false;
  final List<String> generated = [];
  final List<String> deleted = [];
  DeviceKey? key;

  @override
  Future<DeviceKey> generate(String alias) async {
    if (failGenerate) throw Exception('no secure hardware');
    if (throwTypeError) {
      // Like a malformed platform channel payload.
      final Object? bad = null;
      return DeviceKey(kid: alias, x: bad as String, y: 'yy');
    }
    generated.add(alias);
    return key = DeviceKey(kid: alias, x: 'xx', y: 'yy');
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

/// A store whose reads and writes can be made to fail.
class FlakyStore extends MemoryEnrollmentStore {
  bool failWrite = false;
  bool failRead = false;

  @override
  Future<void> write(EnrollmentRecord record) async {
    if (failWrite) throw Exception('secure storage unavailable');
    return super.write(record);
  }

  @override
  Future<EnrollmentRecord?> read() async {
    if (failRead) throw const FormatException('corrupt');
    return super.read();
  }
}

void main() {
  final t0 = DateTime.utc(2026, 10, 19, 9);
  late DateTime clock;
  late FakeKeyStore keys;
  late FlakyStore store;
  late List<http.Request> requests;
  late String lastKid;
  late int activeAtSeconds;
  var acknowledged = false;

  EnrollmentController build(http.Response Function(http.Request) respond) {
    final client = MockClient((r) async {
      requests.add(r);
      // Recovery code endpoints behave like the mock: refused while inactive
      // (rule 7), served after activation, 409 once acknowledged.
      if (r.url.path == '/devices/recovery-code') {
        if (clock.millisecondsSinceEpoch ~/ 1000 < activeAtSeconds) {
          return http.Response('{"error":"access_denied"}', 403);
        }
        if (acknowledged) return http.Response('{"error":"conflict"}', 409);
        return http.Response('{"recovery_code":"AAAA-BBBB-CCCC"}', 200);
      }
      if (r.url.path == '/devices/recovery-code/ack') {
        acknowledged = true;
        return http.Response('{"acknowledged":true}', 200);
      }
      if (r.url.path == '/devices') {
        lastKid = (jsonDecode(r.body) as Map)['kid'] as String;
      }
      return respond(r);
    });
    return EnrollmentController(
      keys: keys,
      store: store,
      client: OidcEnrollmentClient(baseUrl: 'http://mock', client: client),
      clock: () => clock,
    );
  }

  http.Response created({int? activeAt, String? recovery}) {
    activeAtSeconds = activeAt ??
        t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/ 1000;
    return http.Response(
      jsonEncode({
        'kid': lastKid,
        'active': false,
        'active_at': activeAtSeconds,
        'device_token': 'tok',
        if (recovery != null) 'recovery_code': recovery,
      }),
      201,
    );
  }

  setUp(() {
    clock = t0;
    keys = FakeKeyStore();
    store = FlakyStore();
    requests = [];
    acknowledged = false;
    activeAtSeconds = 1 << 40;
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
      final c = build(
          (_) => created(activeAt: t0.millisecondsSinceEpoch ~/ 1000));
      await c.enroll(loginToken: 'login-alice', approvedByKid: 'alice-device');
      // The code is fetched after activation, also for an approved device.
      expect(c.state.phase, EnrollmentPhase.activeRecoveryPending);
      expect(c.recoveryCodeToShow, 'AAAA-BBBB-CCCC');
      expect(
          (jsonDecode(requests.first.body) as Map)['approved_by_kid'],
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
      expect(jsonEncode((await store.read())!.toJson()),
          isNot(contains('AAAA')));
      expect(acknowledged, isTrue);
    });

    test('the recovery code is not requested while inactive', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      await c.refresh();
      expect(requests.map((r) => r.url.path), ['/devices']);
    });

    test('a recovery code in the enrollment response is ignored, not stored',
        () async {
      final c = build((_) => created(recovery: 'EVIL-EVIL-EVIL'));
      await c.enroll(loginToken: 'login-alice');
      expect(c.recoveryCodeToShow, isNull);
      expect(jsonEncode((await store.read())!.toJson()),
          isNot(contains('EVIL')));
      clock = t0.add(const Duration(hours: 24));
      await c.refresh();
      expect(c.recoveryCodeToShow, 'AAAA-BBBB-CCCC');
    });

    test('a failed code fetch can be retried', () async {
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      acknowledged = true; // the server answers 409 now
      clock = t0.add(const Duration(hours: 24));
      await c.refresh();
      expect(c.recoveryCodeToShow, isNull);
      expect(c.recoveryCodeError, contains('409'));
      acknowledged = false;
      await c.loadRecoveryCode();
      expect(c.recoveryCodeToShow, 'AAAA-BBBB-CCCC');
      expect(c.recoveryCodeError, isNull);
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
      expect(keys.deleted, [keys.generated.single]);

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

    test('each install enrolls with its own kid', () async {
      final a = build((_) => created());
      await a.enroll(loginToken: 'login-alice');
      final kidA = lastKid;

      final otherStore = FlakyStore();
      final b = EnrollmentController(
        keys: FakeKeyStore(),
        store: otherStore,
        client: OidcEnrollmentClient(
            baseUrl: 'http://mock',
            client: MockClient((r) async {
              lastKid = (jsonDecode(r.body) as Map)['kid'] as String;
              return created();
            })),
        clock: () => clock,
      );
      await b.enroll(loginToken: 'login-bob');
      expect(lastKid, isNot(kidA));
      expect(kidA, startsWith('biatec-authenticator-'));
      expect(kidA, isNot('biatec-authenticator-device-key'));
      expect(await store.readAlias(), kidA);
      expect(await otherStore.readAlias(), lastKid);
    });

    test('a refusal discards the alias and a retry mints a new one', () async {
      var attempt = 0;
      final c = build((_) =>
          ++attempt == 1 ? http.Response('{"error":"x"}', 403) : created());
      await c.enroll(loginToken: 'bad');
      expect(await store.readAlias(), isNull);
      await c.enroll(loginToken: 'login-alice');
      expect(keys.generated, hasLength(2));
      expect(keys.generated[0], isNot(keys.generated[1]));
    });

    test('a non-object 201 body fails instead of hanging in enrolling',
        () async {
      final c = build((_) => http.Response('[1,2]', 201));
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(c.state.error, isNotNull);
    });

    test('a malformed key from the keystore fails instead of hanging',
        () async {
      keys.throwTypeError = true;
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(requests, isEmpty);
    });

    test('a failed storage write keeps the enrollment and retries later',
        () async {
      store.failWrite = true;
      final c = build((_) => created());
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.inactive);
      expect(c.state.error, contains('could not save'));
      expect(keys.deleted, isEmpty);

      store.failWrite = false;
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.inactive);
      expect(c.state.error, isNull);
      expect((await store.read())!.kid, lastKid);
    });

    test('a corrupt stored record counts as not enrolled', () async {
      store.failRead = true;
      final c = build((_) => created());
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
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
        // The acknowledgement is asynchronous (HTTP call, then storage write).
        await Future<void>.delayed(const Duration(milliseconds: 50));
      });
      await tester.pump();
      expect(find.text('AAAA-BBBB-CCCC'), findsNothing);
      expect(find.text('This device is enrolled and active.'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
