import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/integrity/attestation.dart';
import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

class Keys implements DeviceKeyStore {
  @override
  Future<DeviceKey> generate(String alias) async => DeviceKey(
      kid: alias,
      x: 'xx',
      y: 'yy',
      securityLevel: KeySecurityLevel.trustedEnvironment);

  @override
  Future<DeviceKey?> load(String alias) async => null;

  @override
  Future<Uint8List> sign(String alias, Uint8List data) async => data;

  @override
  Future<void> delete(String alias) async {}
}

class FakeAttestation implements AttestationProvider {
  FakeAttestation({this.canAttest = true, this.token_});
  bool canAttest;
  AttestationToken? token_;
  final nonces = <String>[];
  int supportedCalls = 0;

  /// When set, [token] waits for it (the platform is still working).
  Completer<void>? gate;

  @override
  Future<bool> supported() async {
    supportedCalls++;
    return canAttest;
  }

  @override
  Future<AttestationToken?> token(String nonce) async {
    nonces.add(nonce);
    final g = gate;
    if (g != null) await g.future;
    return token_;
  }
}

const proof = AttestationToken(kind: 'app_attest', token: 'abc', keyId: 'k1');

void main() {
  final t0 = DateTime.utc(2026, 11, 6, 9);
  late DateTime clock;
  late List<http.Request> requests;
  late bool challengeOffered;
  late String lastKid;

  EnrollmentController build(AttestationProvider attestation,
      {EnrollmentStore? store,
      Duration attestationTimeout = const Duration(seconds: 30)}) {
    final client = MockClient((r) async {
      requests.add(r);
      switch ('${r.method} ${r.url.path}') {
        case 'POST /devices/challenge':
          return challengeOffered
              ? http.Response('{"nonce":"nonce-1","expires_in":300}', 200)
              : http.Response('{"error":"not_found"}', 404);
        case 'POST /devices':
          lastKid = (jsonDecode(r.body) as Map)['kid'] as String;
          final attested =
              (jsonDecode(r.body) as Map).containsKey('platform_attestation');
          return http.Response(
              jsonEncode({
                'kid': lastKid,
                'active_at':
                    t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/
                        1000,
                'device_token': 'tok',
                if (attested)
                  'attestation_refresh_after':
                      t0.add(const Duration(days: 30)).millisecondsSinceEpoch ~/
                          1000,
              }),
              201);
        case 'PUT /devices/attestation':
          return http.Response(
              jsonEncode({
                'kid': lastKid,
                'attestation_refresh_after': clock
                        .add(const Duration(days: 30))
                        .millisecondsSinceEpoch ~/
                    1000,
              }),
              200);
      }
      return http.Response('{"error":"not_found"}', 404);
    });
    return EnrollmentController(
      keys: Keys(),
      store: ObservableEnrollmentStore(store ?? MemoryEnrollmentStore(),
          clock: () => clock),
      client: OidcEnrollmentClient(baseUrl: 'http://mock', client: client),
      clock: () => clock,
      attestation: attestation,
      attestationTimeout: attestationTimeout,
    );
  }

  Map<String, Object?> enrollBody() => (jsonDecode(requests
          .firstWhere((r) => r.url.path == '/devices' && r.method == 'POST')
          .body) as Map)
      .cast<String, Object?>();

  setUp(() {
    clock = t0;
    requests = [];
    challengeOffered = true;
  });

  group('enrollment', () {
    test(
        'sends the attestation bound to the challenge when the platform has one',
        () async {
      final a = FakeAttestation(token_: proof);
      final c = build(a);
      await c.enroll(loginToken: 'login-alice');
      expect(a.nonces, ['nonce-1'],
          reason: 'the proof is made for the server\'s nonce');
      final body = enrollBody();
      expect(body['attestation_challenge'], 'nonce-1');
      expect(body['platform_attestation'],
          {'kind': 'app_attest', 'token': 'abc', 'key_id': 'k1'});
      expect(c.state.record!.attestationRefreshAfter,
          t0.add(const Duration(days: 30)));
      // The challenge is asked for with the login, before the enrollment.
      expect(requests.first.url.path, '/devices/challenge');
      expect(requests.first.headers['Authorization'], 'Bearer login-alice');
    });

    test(
        'sends none when the platform cannot attest, and does not ask for a challenge',
        () async {
      final a = FakeAttestation(canAttest: false);
      final c = build(a);
      await c.enroll(loginToken: 'login-alice');
      final body = enrollBody();
      expect(body.containsKey('platform_attestation'), isFalse);
      expect(body.containsKey('attestation_challenge'), isFalse);
      expect(
          requests.where((r) => r.url.path == '/devices/challenge'), isEmpty);
      expect(c.state.record, isNotNull);
      expect(c.state.record!.attestationRefreshAfter, isNull);
    });

    test('sends none, not a made-up one, when the platform gives no token',
        () async {
      final c = build(FakeAttestation());
      await c.enroll(loginToken: 'login-alice');
      final body = enrollBody();
      expect(body.containsKey('platform_attestation'), isFalse);
      expect(body.containsKey('attestation_challenge'), isFalse);
      expect(c.state.record, isNotNull);
    });

    test('enrolls without one when Biatec OIDC offers no challenge', () async {
      challengeOffered = false;
      final a = FakeAttestation(token_: proof);
      final c = build(a);
      await c.enroll(loginToken: 'login-alice');
      expect(a.nonces, isEmpty);
      expect(enrollBody().containsKey('platform_attestation'), isFalse);
      expect(c.state.record, isNotNull);
    });

    test('a platform that never answers does not hold the enrollment',
        () async {
      final a = FakeAttestation(token_: proof)..gate = Completer<void>();
      final c = build(a, attestationTimeout: const Duration(milliseconds: 50));
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.record, isNotNull);
      expect(enrollBody().containsKey('platform_attestation'), isFalse);
    });

    test('a provider that throws does not stop the enrollment', () async {
      final c = build(_Throws());
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.record, isNotNull);
      expect(enrollBody().containsKey('platform_attestation'), isFalse);
    });
  });

  group('the refresh', () {
    Future<(EnrollmentController, MemoryEnrollmentStore)> enrolled(
        FakeAttestation a) async {
      final store = MemoryEnrollmentStore();
      final c = build(a, store: store);
      await c.enroll(loginToken: 'login-alice');
      requests.clear();
      return (c, store);
    }

    test(
        'is sent once the date Biatec OIDC gave has passed, and the new date is saved',
        () async {
      final a = FakeAttestation(token_: proof);
      final (c, store) = await enrolled(a);
      clock = t0.add(const Duration(days: 29));
      await c.refresh();
      expect(
          requests.where((r) => r.url.path == '/devices/attestation'), isEmpty,
          reason: 'not due yet');

      clock = t0.add(const Duration(days: 31));
      await c.refresh();
      final challenge =
          requests.firstWhere((r) => r.url.path == '/devices/challenge');
      expect(challenge.headers['Authorization'], 'Device tok');
      final put =
          requests.firstWhere((r) => r.url.path == '/devices/attestation');
      expect(put.method, 'PUT');
      expect(jsonDecode(put.body), {
        'attestation_challenge': 'nonce-1',
        'platform_attestation': {
          'kind': 'app_attest',
          'token': 'abc',
          'key_id': 'k1'
        },
      });
      expect((await store.read())!.attestationRefreshAfter,
          clock.add(const Duration(days: 30)));
    });

    test('is not sent when the platform cannot attest or gives no token',
        () async {
      final a = FakeAttestation(token_: proof);
      final (c, _) = await enrolled(a);
      clock = t0.add(const Duration(days: 31));
      a.canAttest = false;
      await c.refresh();
      expect(
          requests.where((r) => r.url.path == '/devices/attestation'), isEmpty);
      a.canAttest = true;
      a.token_ = null;
      await c.refresh();
      expect(
          requests.where((r) => r.url.path == '/devices/attestation'), isEmpty);
    });

    test('a device enrolled without one is never asked to refresh', () async {
      final a = FakeAttestation(canAttest: false);
      final (c, _) = await enrolled(a);
      a.canAttest = true;
      a.token_ = proof;
      clock = t0.add(const Duration(days: 400));
      await c.refresh();
      expect(
          requests.where((r) => r.url.path == '/devices/attestation'), isEmpty);
    });

    test('the new date goes on the record as it is now, not as it was',
        () async {
      final a = FakeAttestation(token_: proof);
      final (c, store) = await enrolled(a);
      a.gate = Completer<void>();
      clock = t0.add(const Duration(days: 31));
      final refreshing = c.refresh();
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      // While the platform is still working the user confirms the recovery code.
      final now = (await store.read())!;
      await store.write(now.withCodeShown());
      a.gate!.complete();
      await refreshing;
      final saved = (await store.read())!;
      expect(saved.recoveryCodeShown, isTrue,
          reason: 'not undone by the late write');
      expect(
          saved.attestationRefreshAfter, clock.add(const Duration(days: 30)));
    });

    test('a failed refresh waits an hour before the next try', () async {
      final a = FakeAttestation(token_: proof);
      final (c, _) = await enrolled(a);
      clock = t0.add(const Duration(days: 31));
      challengeOffered = false;
      await c.refresh();
      await c.refresh();
      expect(requests.where((r) => r.url.path == '/devices/challenge'),
          hasLength(1),
          reason: 'the second resume does not ask again');
      clock = clock.add(const Duration(minutes: 61));
      challengeOffered = true;
      await c.refresh();
      expect(requests.where((r) => r.url.path == '/devices/attestation'),
          hasLength(1));
    });

    test('a failing server leaves things as they were', () async {
      final a = FakeAttestation(token_: proof);
      final (c, store) = await enrolled(a);
      challengeOffered = false;
      clock = t0.add(const Duration(days: 31));
      await c.refresh();
      expect((await store.read())!.attestationRefreshAfter,
          t0.add(const Duration(days: 30)));
      expect(c.state.record, isNotNull);
    });
  });

  group('the record', () {
    test(
        'keeps the refresh date through a save and a read, and an old record has none',
        () {
      final r = EnrollmentRecord(
          kid: 'k',
          deviceToken: 't',
          activeAt: t0,
          attestationRefreshAfter: t0.add(const Duration(days: 30)));
      expect(EnrollmentRecord.fromJson(r.toJson()).attestationRefreshAfter,
          t0.add(const Duration(days: 30)));
      expect(
          r.withCodeShown().attestationRefreshAfter, r.attestationRefreshAfter);
      final old = EnrollmentRecord.fromJson({
        'kid': 'k',
        'device_token': 't',
        'active_at': t0.millisecondsSinceEpoch,
      });
      expect(old.attestationRefreshAfter, isNull);
    });
  });

  group('platform wiring', () {
    String read(String path) => File(path).readAsStringSync();

    test(
        'both platforms implement the attestation channel and it is registered',
        () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/AttestationChannel.kt');
      expect(kt, contains('io.biatec.authenticator/attestation'));
      expect(kt, contains('IntegrityManagerFactory'));
      expect(kt, contains('setCloudProjectNumber'));
      expect(kt, contains('SHA-256'), reason: 'the nonce is bound by its hash');
      expect(
          read(
              'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt'),
          contains('AttestationChannel('));
      final sw = read('ios/Runner/IntegrityChannel.swift');
      expect(sw, contains('io.biatec.authenticator/attestation'));
      expect(sw, contains('attestKey'));
      expect(sw, contains('isSupported'));
    });

    test(
        'the Play Integrity dependency and the project number come from the build',
        () {
      final gradle = read('android/app/build.gradle.kts');
      expect(gradle, contains('com.google.android.play:integrity'));
      expect(gradle, contains('playIntegrityProject'));
      expect(read('android/app/src/main/AndroidManifest.xml'),
          contains('io.biatec.authenticator.PLAY_INTEGRITY_PROJECT'));
    });

    test('no project number is in the source', () {
      expect(read('android/app/build.gradle.kts'), contains('?: "0"'));
    });
  });

  group('the provider', () {
    test('the attestation token names nothing when printed', () {
      expect(proof.toString(), isNot(contains('abc')));
    });
  });
}

class _Throws implements AttestationProvider {
  @override
  Future<bool> supported() async => throw StateError('boom');

  @override
  Future<AttestationToken?> token(String nonce) async =>
      throw StateError('boom');
}
