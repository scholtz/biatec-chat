// T-242: the in-app login (a fake auth client in place of the system browser), the stale-login restart, the pairing code a waiting device shows, the recovery
// code at enrollment, and the waiting device's polling and cancel.
import 'dart:async';
import 'dart:convert';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_strings.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/enrollment/pairing_code.dart';
import 'package:biatec_authenticator/enrollment/widgets/enrollment_problem.dart';
import 'package:biatec_authenticator/integrity/attestation.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../keys/fake_two_keys.dart';

class _NoAttestation implements AttestationProvider {
  @override
  Future<bool> supported() async => false;
  @override
  Future<AttestationToken?> token(String nonce) async => null;
}

/// The system browser stand-in: records what it was asked and plays a script of tokens and failures.
class FakeAuth extends AuthClient {
  FakeAuth(this.script);

  final List<Object> script;
  final List<AuthRequest> requests = [];

  @override
  Future<String> signIn(AuthRequest request) async {
    requests.add(request);
    final next = script[requests.length - 1];
    if (next is Exception) throw next;
    return next as String;
  }
}

/// An in-memory secure storage that remembers every value ever written to it.
class _Storage extends FlutterSecureStorage {
  final Map<String, String> items = {};
  final List<String> everWritten = [];
  bool failDelete = false;

  @override
  Future<String?> read(
          {required String key,
          iOptions,
          aOptions,
          lOptions,
          webOptions,
          mOptions,
          wOptions}) async =>
      items[key];

  @override
  Future<void> write(
      {required String key,
      required String? value,
      iOptions,
      aOptions,
      lOptions,
      webOptions,
      mOptions,
      wOptions}) async {
    if (value == null) {
      items.remove(key);
    } else {
      items[key] = value;
      everWritten.add(value);
    }
  }

  @override
  Future<void> delete(
      {required String key,
      iOptions,
      aOptions,
      lOptions,
      webOptions,
      mOptions,
      wOptions}) async {
    if (failDelete) throw StateError('keychain locked');
    items.remove(key);
  }
}

/// A keystore that cannot delete.
class _StuckKeys extends FakeTwoKeyStore {
  @override
  Future<void> deleteKeys() async => throw StateError('keystore busy');
}

/// A browser that answers when the test says so.
class _SlowAuth extends FakeAuth {
  _SlowAuth(this.gate) : super([]);
  final Completer<String> gate;
  @override
  Future<String> signIn(AuthRequest request) {
    requests.add(request);
    return gate.future;
  }
}

void main() {
  final t0 = DateTime.utc(2026, 11, 1, 9);
  const nonce = 'enroll-nonce-0123456789abcdef';

  late List<http.Request> requests;
  late FakeTwoKeyStore keys;
  late _Storage storage;
  late FakeAuth auth;

  /// A Biatec OIDC that accepts `good-token`, finds `stale-token` too old, answers [enrollStatus] to `POST /devices` and lists [devices].
  EnrollmentController build({
    int enrollStatus = 201,
    Map<String, Object?>? enrollBody,
    List<Map<String, Object?>> Function()? devices,
    DateTime? activeAt,
    int devicesStatus = 200,
    int cancelStatus = 200,
  }) {
    final client = MockClient((r) async {
      requests.add(r);
      final token = r.headers['Authorization'];
      if (r.url.path == '/devices/challenge') {
        if (token == 'Bearer stale-token') {
          return http.Response(
              jsonEncode({
                'error': 'insufficient_user_authentication',
                'max_age': 0
              }),
              401);
        }
        return http.Response(jsonEncode({'nonce': nonce}), 200);
      }
      if (r.method == 'POST' && r.url.path == '/devices') {
        if (token == 'Bearer stale-token') {
          return http.Response(
              jsonEncode({'error': 'insufficient_user_authentication'}), 401);
        }
        if (enrollStatus != 201) {
          return http.Response(jsonEncode(enrollBody ?? {}), enrollStatus);
        }
        final body = jsonDecode(r.body) as Map;
        return http.Response(
            jsonEncode({
              'kid': body['kid'],
              'active_at': (activeAt ?? t0.add(const Duration(hours: 24)))
                      .millisecondsSinceEpoch ~/
                  1000,
              'device_token': 'device-token-1',
              'attestation': 'a.b.c',
              'presence_kid': body['presence_kid'],
              'presence_attestation': 'p.b.c',
            }),
            201);
      }
      if (r.method == 'GET' && r.url.path == '/devices') {
        if (devicesStatus != 200) {
          return http.Response(jsonEncode({'error': 'invalid_token'}), devicesStatus);
        }
        return http.Response(
            jsonEncode({'devices': devices?.call() ?? <Object>[]}), 200);
      }
      if (r.method == 'POST' && r.url.path.endsWith('/cancel')) {
        return http.Response(jsonEncode({'cancelled': cancelStatus == 200}), cancelStatus);
      }
      return http.Response('{}', 404);
    });
    return EnrollmentController(
      keys: keys,
      store: ObservableEnrollmentStore(SecureEnrollmentStore(storage)),
      client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
      clock: () => t0,
      attestation: _NoAttestation(),
      twoKeys: true,
      auth: auth,
      loginRequest: AuthRequest.forConfig(AppConfig.resolve('')),
      devices: SettingsClient(baseUrl: 'http://oidc', client: client),
    );
  }

  setUp(() {
    requests = [];
    keys = FakeTwoKeyStore();
    storage = _Storage();
    auth = FakeAuth(['good-token']);
  });

  group('the login request', () {
    test('asks for exactly openid enroll-device, prompt=login and max_age=0',
        () async {
      final c = build();
      final result = await c.signInAndEnroll();
      expect(result, EnrollResult.enrolled, reason: c.state.error);
      final r = auth.requests.single;
      expect(r.scope, 'openid enroll-device');
      expect(r.prompt, 'login');
      expect(r.maxAge, 0);
      // No refresh token is asked for.
      expect(r.scope.split(' '), isNot(contains('offline_access')));
      expect(r.clientId, AppConfig.defaultOidcClientId);
      // A debug build returns through the custom scheme, which a release build never uses.
      expect(r.redirectUri, AppConfig.debugRedirectUri);
    });

    test('the mock is addressed by its endpoints, a real Biatec OIDC by discovery',
        () {
      final mock = AuthRequest.forConfig(AppConfig.resolve(''));
      expect(mock.authorizationEndpoint, endsWith('/authorize'));
      expect(mock.tokenEndpoint, endsWith('/token'));
      expect(mock.discoveryUrl, isNull);
      expect(mock.allowInsecure, isTrue);
      final real = AuthRequest.forConfig(AppConfig.resolve(
          'https://oidc.biatec.io',
          release: true,
          inAppLogin: true,
          clientId: 'my-client'));
      expect(real.discoveryUrl,
          'https://oidc.biatec.io/.well-known/openid-configuration');
      expect(real.authorizationEndpoint, isNull);
      expect(real.allowInsecure, isFalse);
      expect(real.clientId, 'my-client');
      expect(real.redirectUri, AppConfig.appLinkRedirectUri);
      expect(real.scope, 'openid enroll-device');
    });
  });

  group('the token', () {
    test('is used for the challenge and the enrollment, and written to no store',
        () async {
      auth = FakeAuth(['secret-login-token-123456']);
      final c = build();
      await c.signInAndEnroll();
      expect(c.state.phase, EnrollmentPhase.inactive, reason: c.state.error);
      final calls = requests
          .where((r) =>
              r.url.path == '/devices/challenge' || r.url.path == '/devices')
          .toList();
      expect(calls, hasLength(2));
      for (final r in calls) {
        expect(r.headers['Authorization'], 'Bearer secret-login-token-123456');
      }
      // The fake secure storage holds the enrollment record and nothing else, and no value in it (ever written, or kept) has the token.
      expect(storage.items.keys,
          everyElement(anyOf('biatec.enrollment', 'biatec.enrollment.alias')));
      for (final v in [...storage.everWritten, ...storage.items.values]) {
        expect(v, isNot(contains('secret-login-token')));
      }
      expect(c.state.error, isNull);
      expect(c.state.record!.toJson().toString(),
          isNot(contains('secret-login-token')));
    });

    test('a cancelled sign-in enrolls nothing and says so', () async {
      auth = FakeAuth([const LoginCancelled()]);
      final c = build();
      final result = await c.signInAndEnroll();
      expect(result, EnrollResult.failed);
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(c.state.error, EnrollmentController.loginCancelledError);
      expect(requests, isEmpty);
      expect(storage.items, isEmpty);
      expect(keys.generated, isEmpty);
      expect(classifyEnrollmentError(c.state.error!).kind,
          EnrollmentProblemKind.signInFailed);
    });

    test('a failed sign-in says why and never carries a token', () async {
      auth = FakeAuth([const LoginFailed('server_error')]);
      final c = build();
      await c.signInAndEnroll();
      expect(c.state.error, '${EnrollmentController.loginFailedPrefix} server_error');
      expect(requests, isEmpty);
    });
  });

  group('a login that is too old', () {
    test('restarts the login once and then enrolls', () async {
      auth = FakeAuth(['stale-token', 'good-token']);
      final c = build();
      final result = await c.signInAndEnroll();
      expect(result, EnrollResult.enrolled, reason: c.state.error);
      expect(auth.requests, hasLength(2));
      // The stale token was refused at the challenge: it never reached POST /devices.
      final enrolls = requests.where(
          (r) => r.method == 'POST' && r.url.path == '/devices').toList();
      expect(enrolls, hasLength(1));
      expect(enrolls.single.headers['Authorization'], 'Bearer good-token');
      // The keys of the refused attempt were removed before the second one.
      expect(keys.deleteCalls, greaterThanOrEqualTo(1));
    });

    test('is not restarted a second time', () async {
      auth = FakeAuth(['stale-token', 'stale-token', 'good-token']);
      final c = build();
      final result = await c.signInAndEnroll();
      expect(result, EnrollResult.failed);
      expect(auth.requests, hasLength(2), reason: 'once, not forever');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(c.state.error, EnrollmentController.staleLoginError);
      expect(storage.items, isEmpty);
    });

    test('enroll() alone reports it, with the "sign in again" notice', () async {
      final c = build();
      final result = await c.enroll(loginToken: 'stale-token');
      expect(result, EnrollResult.staleLogin);
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(c.state.notice, isNotNull);
      expect(c.state.error, isNull);
    });
  });

  group('waiting for another device', () {
    test('the pairing code comes from this device\'s own keys and its nonce',
        () async {
      final c = build();
      await c.signInAndEnroll();
      final record = c.state.record!;
      final k = keys.keys!;
      expect(record.pairingCode,
          pairingCode(k.approval.thumbprint, k.presence.thumbprint, nonce));
      expect(record.pairingCode, matches(RegExp(r'^\d{6}$')));
      // It survives a restart (it is in the stored record, which holds no secret of the login).
      final again = EnrollmentRecord.fromJson(record.toJson());
      expect(again.pairingCode, record.pairingCode);
    });

    test('polling GET /devices turns the device active, and the recovery code follows',
        () async {
      var active = false;
      late EnrollmentController c;
      c = build(devices: () {
        final kid = c.state.record!.kid;
        return [
          {
            'kid': kid,
            'current': true,
            'active': active,
            'active_at': t0.millisecondsSinceEpoch ~/ 1000 +
                (active ? 0 : 86400),
          }
        ];
      });
      await c.signInAndEnroll();
      expect(c.state.phase, EnrollmentPhase.inactive);
      await c.pollActivation();
      expect(c.state.phase, EnrollmentPhase.inactive, reason: 'not yet');
      active = true;
      await c.pollActivation();
      expect(c.state.phase, EnrollmentPhase.activeRecoveryPending);
      expect((await c.store.read())!.activeAt.isAfter(t0), isFalse);
    });

    test('"Cancel this device" cancels at Biatec OIDC and removes the enrollment',
        () async {
      final c = build();
      await c.signInAndEnroll();
      final kid = c.state.record!.kid;
      expect(await c.cancelThisDevice(), isTrue);
      final cancel = requests.lastWhere((r) => r.url.path.endsWith('/cancel'));
      expect(cancel.url.path, '/devices/$kid/cancel');
      expect(cancel.headers['Authorization'], 'Device device-token-1');
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(storage.items, isEmpty);
      expect(keys.keys, isNull);
    });
  });

  group('cancelling this device', () {
    test('ends signed out even when the local clean-up fails', () async {
      keys = _StuckKeys();
      final c = build();
      await c.signInAndEnroll();
      storage.failDelete = true;
      expect(await c.cancelThisDevice(), isTrue);
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(c.cancelError, isNull);
    });

    test('says so only when the server could not cancel it, and stays enrolled',
        () async {
      final c = build();
      await c.signInAndEnroll();
      final kid = c.state.record!.kid;
      // Biatec OIDC unreachable: swap the clients' answer by cancelling a device of the wrong state is not needed, an unknown token is enough.
      final broken = EnrollmentController(
        keys: keys,
        store: c.store,
        client: OidcEnrollmentClient(baseUrl: 'http://oidc'),
        devices: SettingsClient(
            baseUrl: 'http://oidc',
            client: MockClient((_) async => http.Response('{}', 500))),
      );
      await broken.refresh();
      expect(await broken.cancelThisDevice(), isFalse);
      expect(broken.cancelError, isNotNull);
      expect(broken.state.record!.kid, kid);
    });
  });

  group('a device cancelled elsewhere', () {
    for (final status in [401, 403, 404]) {
      test('a $status on the poll signs it out with a notice and removes the keys',
          () async {
        final c = build(devicesStatus: status);
        await c.signInAndEnroll();
        expect(c.state.phase, EnrollmentPhase.inactive);
        await c.pollActivation();
        expect(c.state.phase, EnrollmentPhase.notEnrolled);
        expect(c.state.notice, EnrollStrings.of().deviceCancelled);
        expect(c.state.record, isNull);
        expect(storage.items, isEmpty);
        expect(keys.keys, isNull);
        // A later refresh does not wipe the notice.
        await c.refresh();
        expect(c.state.notice, isNotNull);
      });
    }

    test('a device that is no longer listed was cancelled', () async {
      final c = build(devices: () => []);
      await c.signInAndEnroll();
      await c.pollActivation();
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(c.state.notice, EnrollStrings.of().deviceCancelled);
      expect(storage.items, isEmpty);
    });

    test('"Cancel this device" on one that is already cancelled ends clean, with the notice',
        () async {
      final c = build(cancelStatus: 404);
      await c.signInAndEnroll();
      expect(await c.cancelThisDevice(), isTrue);
      expect(c.cancelError, isNull);
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(c.state.notice, EnrollStrings.of().deviceCancelled);
      expect(storage.items, isEmpty);
    });

    test('a server error (500) on cancel is an error, not a cancellation', () async {
      final c = build(cancelStatus: 500);
      await c.signInAndEnroll();
      expect(await c.cancelThisDevice(), isFalse);
      expect(c.state.phase, EnrollmentPhase.inactive);
      expect(c.cancelError, isNotNull);
    });
  });

  group('the browser flow outlives the screen', () {
    test('a disposed controller enrolls nothing and changes nothing', () async {
      final gate = Completer<String>();
      auth = _SlowAuth(gate);
      final c = build();
      var notified = 0;
      c.addListener(() => notified++);
      final pending = c.signInAndEnroll();
      await Future<void>.delayed(Duration.zero);
      expect(c.signingIn, isTrue);
      c.dispose();
      final before = notified;
      gate.complete('good-token');
      expect(await pending, EnrollResult.ignored);
      expect(notified, before);
      expect(requests, isEmpty, reason: 'the token was dropped, nothing was sent');
      expect(keys.generated, isEmpty);
      expect(storage.items, isEmpty);
    });

    test('a cancelled sign-in after dispose is quiet too', () async {
      final gate = Completer<String>();
      auth = _SlowAuth(gate);
      final c = build();
      final pending = c.signInAndEnroll();
      await Future<void>.delayed(Duration.zero);
      c.dispose();
      gate.completeError(const LoginCancelled());
      expect(await pending, EnrollResult.ignored);
    });
  });

  group('a recovery code that cannot be one', () {
    test('is refused before the browser opens, and nothing is sent', () async {
      for (final typed in ['WRONG', '1234', 'ZZZZ-ZZZZ-ZZZZ', '1A2B-3C4D-5E6F-7']) {
        final c = build();
        final result = await c.signInAndEnroll(recoveryCode: typed);
        expect(result, EnrollResult.failed, reason: typed);
        expect(c.state.error, EnrollmentController.recoveryFormatError);
        expect(classifyEnrollmentError(c.state.error!).kind,
            EnrollmentProblemKind.wrongRecoveryCode);
      }
      expect(auth.requests, isEmpty);
      expect(requests, isEmpty);
    });

    test('enroll() with a pasted token checks it too, before any key is made', () async {
      final c = build();
      expect(await c.enroll(loginToken: 'good-token', recoveryCode: 'nope'),
          EnrollResult.failed);
      expect(keys.generated, isEmpty);
      expect(requests, isEmpty);
    });

    test('typed loosely is sent in the form Biatec OIDC takes', () async {
      final c = build(activeAt: t0);
      await c.signInAndEnroll(recoveryCode: ' 1a2b 3c4d-5e6f ');
      final body = jsonDecode(requests
          .lastWhere((r) => r.method == 'POST' && r.url.path == '/devices')
          .body) as Map;
      expect(body['activation'], {'kind': 'recovery_code', 'code': '1A2B-3C4D-5E6F'});
    });

    test('an empty field is no code', () async {
      final c = build(activeAt: t0);
      await c.signInAndEnroll(recoveryCode: '  ');
      final body = jsonDecode(requests
          .lastWhere((r) => r.method == 'POST' && r.url.path == '/devices')
          .body) as Map;
      expect(body.containsKey('activation'), isFalse);
    });
  });

  group('the recovery code at enrollment', () {
    test('goes in activation, and the device is active at once', () async {
      final c = build(activeAt: t0);
      final result = await c.signInAndEnroll(recoveryCode: 'AAAA-BBBB-CCCC');
      expect(result, EnrollResult.enrolled, reason: c.state.error);
      final body = jsonDecode(requests
          .lastWhere((r) => r.method == 'POST' && r.url.path == '/devices')
          .body) as Map;
      expect(body['activation'],
          {'kind': 'recovery_code', 'code': 'AAAA-BBBB-CCCC'});
      expect(body.containsKey('approved_by_kid'), isFalse);
    });

    test('a wrong code has its own text and nothing is enrolled', () async {
      final c = build(
          enrollStatus: 403, enrollBody: {'error': 'invalid_recovery_code'});
      final result = await c.signInAndEnroll(recoveryCode: '0000-0000-0000');
      expect(result, EnrollResult.failed);
      expect(c.state.error, EnrollmentController.recoveryWrongError);
      final p = classifyEnrollmentError(c.state.error!);
      expect(p.kind, EnrollmentProblemKind.wrongRecoveryCode);
      expect(storage.items, isEmpty);
    });

    test('a plain 429 (a rate limit) is not the recovery lockout, with or without a code',
        () async {
      for (final code in [null, 'AAAA-BBBB-CCCC']) {
        auth = FakeAuth(['good-token']);
        final c = build(enrollStatus: 429, enrollBody: {'error': 'rate_limited'});
        await c.signInAndEnroll(recoveryCode: code);
        expect(c.state.error, isNot(EnrollmentController.recoveryLockedError));
      }
      // The lockout needs both the code in the request and the server's error code.
      auth = FakeAuth(['good-token']);
      final noCode = build(
          enrollStatus: 429, enrollBody: {'error': 'too_many_attempts'});
      await noCode.signInAndEnroll();
      expect(noCode.state.error,
          isNot(EnrollmentController.recoveryLockedError));
    });

    test('too many wrong codes (429) have their own text', () async {
      final c =
          build(enrollStatus: 429, enrollBody: {'error': 'too_many_attempts'});
      await c.signInAndEnroll(recoveryCode: '0000-0000-0000');
      expect(c.state.error, EnrollmentController.recoveryLockedError);
      expect(classifyEnrollmentError(c.state.error!).kind,
          EnrollmentProblemKind.recoveryLocked);
      expect(EnrollmentController.recoveryLockedError,
          isNot(EnrollmentController.recoveryWrongError));
    });
  });
}
