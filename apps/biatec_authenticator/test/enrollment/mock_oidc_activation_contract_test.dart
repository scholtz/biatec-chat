// Contract test (T-242): the in-app login, the activation of a new device by an existing one, and the recovery code, against a running
// packages/consent-protocol/mock-oidc server (no hand-written responses).
//
//   MOCK_TWO_KEYS=1 MOCK_JSON_GROUPS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 PORT=8089 pnpm --filter mock-oidc start   # from the repo root; 127.0.0.1:8089
//   flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8089 test/enrollment/mock_oidc_activation_contract_test.dart
//
// Skipped when MOCK_OIDC_URL is not set.
import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/auth_client.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../approvals/support.dart';

class _Pass implements BiometricGate {
  @override
  Future<bool> confirm(String reason) async => true;
}

/// The system browser, played by Dart: it makes the authorization request the app makes (from the same [AuthRequest]), takes the code out of the redirect and exchanges it with a PKCE verifier. It is the mock's own login, so a token the mock hands out is what the app would get.
class _MockBrowser extends AuthClient {
  _MockBrowser(this.base);

  final String base;
  static const hint = 'alice@example.com';
  final List<AuthRequest> requests = [];

  @override
  Future<String> signIn(AuthRequest r) async {
    requests.add(r);
    final random = Random.secure();
    final verifier = base64Url
        .encode([for (var i = 0; i < 32; i++) random.nextInt(256)])
        .replaceAll('=', '');
    final challenge = base64Url
        .encode(sha256.convert(utf8.encode(verifier)).bytes)
        .replaceAll('=', '');
    final authUrl = Uri.parse(r.authorizationEndpoint!).replace(queryParameters: {
      'response_type': 'code',
      'client_id': r.clientId,
      'redirect_uri': r.redirectUri,
      'scope': r.scope,
      'state': 's1',
      'code_challenge': challenge,
      'code_challenge_method': 'S256',
      'prompt': r.prompt,
      'max_age': '${r.maxAge}',
      'login_hint': hint,
    });
    final request = http.Request('GET', authUrl)..followRedirects = false;
    final res = await http.Response.fromStream(await http.Client().send(request));
    if (res.statusCode != 302) throw LoginFailed('authorize: ${res.body}');
    final back = Uri.parse(res.headers['location']!);
    if (back.queryParameters['state'] != 's1') throw const LoginFailed('state');
    final code = back.queryParameters['code'];
    if (code == null) throw LoginFailed('${back.queryParameters}');
    final token = await http.post(Uri.parse(r.tokenEndpoint!), body: {
      'grant_type': 'authorization_code',
      'client_id': r.clientId,
      'code': code,
      'redirect_uri': r.redirectUri,
      'code_verifier': verifier,
    });
    if (token.statusCode != 200) throw LoginFailed('token: ${token.body}');
    final json = jsonDecode(token.body) as Map;
    expect(json.containsKey('refresh_token'), isFalse);
    expect(json['scope'], 'openid enroll-device');
    return json['access_token'] as String;
  }
}

class _Script extends AuthClient {
  _Script(this.tokens);
  final List<String> tokens;
  int calls = 0;
  @override
  Future<String> signIn(AuthRequest request) async => tokens[calls++];
}

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;
  final config = AppConfig.resolve('');

  /// An install: its own random software keys, an in-memory store, the real clients.
  ({EnrollmentController c, SoftwareKeyStore keys, ObservableEnrollmentStore store}) install(
      AuthClient auth) {
    final keys = randomSoftwareKeyStore();
    final store = ObservableEnrollmentStore(MemoryEnrollmentStore());
    final c = EnrollmentController(
      keys: keys,
      store: store,
      client: OidcEnrollmentClient(baseUrl: baseUrl!),
      auth: auth,
      loginRequest: AuthRequest(
          clientId: config.oidcClientId,
          redirectUri: config.redirectUri,
          authorizationEndpoint: '$baseUrl/authorize',
          tokenEndpoint: '$baseUrl/token',
          allowInsecure: true),
      devices: SettingsClient(baseUrl: baseUrl),
    );
    return (c: c, keys: keys, store: store);
  }

  /// An install that is active at once, the way a later device is after its delay (the mock's test helper skips it).
  Future<({EnrollmentController c, SoftwareKeyStore keys, ObservableEnrollmentStore store})>
      activeInstall(String login) async {
    final i = install(_Script([login]));
    expect(await i.c.signInAndEnroll(), EnrollResult.enrolled,
        reason: i.c.state.error);
    await http
        .post(Uri.parse('$baseUrl/mock/devices/${i.c.state.record!.kid}/activate'));
    await i.c.pollActivation();
    expect(i.c.state.phase, EnrollmentPhase.activeRecoveryPending,
        reason: i.c.state.error);
    return i;
  }

  SettingsController settingsOf(
          ({EnrollmentController c, SoftwareKeyStore keys, ObservableEnrollmentStore store}) i) =>
      SettingsController(
          client: SettingsClient(baseUrl: baseUrl!),
          keys: i.keys,
          enrollment: i.store,
          gate: _Pass());

  /// What `GET /devices` lists for the device [kid], as seen by the approver [i].
  Future<DeviceInfo> listed(
      ({EnrollmentController c, SoftwareKeyStore keys, ObservableEnrollmentStore store}) i,
      String kid) async {
    final list = await SettingsClient(baseUrl: baseUrl!)
        .devices(i.c.state.record!.deviceToken);
    return list.firstWhere((d) => d.kid == kid);
  }

  /// A device approval built by hand for [target], signed by [signWith] (`approval` or `presence`), with [over] applied to the signed members.
  Future<Map<String, Object?>> approvalBy(
    ({EnrollmentController c, SoftwareKeyStore keys, ObservableEnrollmentStore store}) approver,
    DeviceInfo target, {
    String signWith = 'approval',
    Map<String, Object?> over = const {},
  }) async {
    final record = approver.c.state.record!;
    final pairing = target.pairing!;
    final presence = signWith == 'presence';
    final key = presence
        ? (await approver.keys.loadPresence())!
        : (await approver.keys.loadApproval())!;
    final signed = <String, Object?>{
      'type': 'biatec_device_approval',
      'approver_kid': presence ? record.presenceKid : record.kid,
      'new_kid': target.kid,
      'new_approval_jwk_thumbprint': pairing.approvalThumbprint,
      'new_presence_jwk_thumbprint': pairing.presenceThumbprint,
      'pairing_code': SettingsController.pairingCodeOf(target),
      'nonce': pairing.nonce,
      'issued_at': DateTime.now().toUtc().millisecondsSinceEpoch ~/ 1000,
      ...over,
    };
    final data = Uint8List.fromList(canonicalBytes(signed));
    final sig = presence
        ? await approver.keys.signPresence(data)
        : await approver.keys.signApproval(data,
            promptTitle: 'test', promptSubtitle: 'test');
    return {
      'signed': signed,
      'alg': 'ES256',
      'signature': base64Url.encode(sig).replaceAll('=', ''),
      'device': {
        'kid': presence ? record.presenceKid : record.kid,
        'key_use': presence ? 'presence' : 'approve',
        'public_key': key.toJwk(),
        'attestation': presence ? record.presenceAttestation : record.attestation,
      },
    };
  }

  test('login: the mock\'s authorize and token endpoints give a token that enrolls a device',
      () async {
    final browser = _MockBrowser(baseUrl!);
    final i = install(browser);
    expect(await i.c.signInAndEnroll(), EnrollResult.enrolled,
        reason: i.c.state.error);
    final r = browser.requests.single;
    expect(r.scope, 'openid enroll-device');
    expect(r.prompt, 'login');
    expect(r.maxAge, 0);
    expect(i.c.state.phase, EnrollmentPhase.inactive);
    expect(i.c.state.record!.pairingCode, matches(RegExp(r'^\d{6}$')));
  }, skip: skip);

  test('a stale login is refused by the mock and the app restarts the login once',
      () async {
    final script = _Script(['login-alice-stale', 'login-alice']);
    final i = install(script);
    expect(await i.c.signInAndEnroll(), EnrollResult.enrolled,
        reason: i.c.state.error);
    expect(script.calls, 2);
    // Twice stale: it ends, it does not loop.
    final again = _Script(['login-alice-stale', 'login-alice-stale', 'login-alice']);
    final j = install(again);
    expect(await j.c.signInAndEnroll(), EnrollResult.failed);
    expect(again.calls, 2);
    expect(j.c.state.error, EnrollmentController.staleLoginError);
  }, skip: skip);

  test('confirm: the pairing codes agree, the approver signs, and the new device becomes active',
      () async {
    final approver = await activeInstall('login-alice');
    final fresh = install(_Script(['login-alice']));
    expect(await fresh.c.signInAndEnroll(), EnrollResult.enrolled);
    expect(fresh.c.state.phase, EnrollmentPhase.inactive);

    final settings = settingsOf(approver);
    await settings.load();
    final pending =
        settings.devices.firstWhere((d) => d.kid == fresh.c.state.record!.kid);
    // The code the approver works out from what the server lists is the one the new device worked out from its own keys.
    expect(SettingsController.pairingCodeOf(pending),
        fresh.c.state.record!.pairingCode);
    expect(pending.name, EnrollmentController.defaultDeviceName);

    expect(await settings.activateDevice(pending), isTrue,
        reason: settings.error);
    await fresh.c.pollActivation();
    expect(fresh.c.state.phase, EnrollmentPhase.activeRecoveryPending,
        reason: fresh.c.state.error);
  }, skip: skip);

  test('refused: a presence-key signature, a wrong pairing code, an approver of another account, a replayed approval',
      () async {
    final approver = await activeInstall('login-alice');
    final other = await activeInstall('login-carol');
    final fresh = install(_Script(['login-alice']));
    await fresh.c.signInAndEnroll();
    final target = await listed(approver, fresh.c.state.record!.kid);
    final api = SettingsClient(baseUrl: baseUrl!);
    final token = approver.c.state.record!.deviceToken;
    final kid = target.kid;

    expect(
        api.activateDevice(token, kid,
            await approvalBy(approver, target, signWith: 'presence')),
        throwsA(isA<ApprovalException>()),
        reason: 'the presence key never approves');
    expect(
        api.activateDevice(token, kid,
            await approvalBy(approver, target, over: {'pairing_code': '000000'})),
        throwsA(isA<ApprovalException>()),
        reason: 'a pairing code the user did not compare');
    expect(
        api.activateDevice(
            token,
            kid,
            await approvalBy(approver, target,
                over: {'new_kid': 'another-kid'})),
        throwsA(isA<ApprovalException>()));
    expect(
        api.activateDevice(
            token,
            kid,
            await approvalBy(approver, target, over: {
              'issued_at':
                  DateTime.now().toUtc().millisecondsSinceEpoch ~/ 1000 - 3600
            })),
        throwsA(isA<ApprovalException>()),
        reason: 'a statement from an hour ago');
    // Carol's device is active, but it is another account's: the pending device is not even found for it.
    final byOther = SettingsClient(baseUrl: baseUrl);
    await expectLater(
        byOther.activateDevice(other.c.state.record!.deviceToken, kid,
            await approvalBy(other, target)),
        throwsA(isA<ApprovalException>().having(
            (e) => e.message, 'message', contains('no longer waiting'))));
    // Nothing above activated it.
    await fresh.c.pollActivation();
    expect(fresh.c.state.phase, EnrollmentPhase.inactive);

    // The right statement works once; the same statement again is refused (the device is active, and the nonce is used).
    final good = await approvalBy(approver, target);
    await api.activateDevice(token, kid, good);
    await expectLater(api.activateDevice(token, kid, good),
        throwsA(isA<ApprovalException>()));
    await fresh.c.pollActivation();
    expect(fresh.c.state.phase, EnrollmentPhase.activeRecoveryPending);
  }, skip: skip);

  test('approved_by_kid is refused: naming an active device proves nothing',
      () async {
    final approver = await activeInstall('login-alice');
    final res = await http.post(Uri.parse('$baseUrl/devices'),
        headers: {
          'Authorization': 'Bearer login-alice',
          'Content-Type': 'application/json'
        },
        body: jsonEncode({
          'kid': 'x-${DateTime.now().microsecondsSinceEpoch}',
          'public_key': {'kty': 'EC', 'crv': 'P-256', 'x': 'A' * 43, 'y': 'B' * 43},
          'approved_by_kid': approver.c.state.record!.kid,
        }));
    expect(res.statusCode, 400);
    expect(jsonDecode(res.body)['error_description'],
        contains('approved_by_kid'));
  }, skip: skip);

  test('recovery code: activates at once, once; a wrong code and a lockout have their own refusals',
      () async {
    final first = await activeInstall('login-alice');
    final code = first.c.recoveryCodeToShow;
    expect(code, isNotNull, reason: first.c.recoveryCodeError);
    await first.c.acknowledgeRecoveryCode();

    final ok = install(_Script(['login-alice']));
    expect(await ok.c.signInAndEnroll(recoveryCode: code),
        EnrollResult.enrolled, reason: ok.c.state.error);
    expect(ok.c.state.phase, EnrollmentPhase.activeRecoveryPending,
        reason: 'active at once, no 24 hours');

    // The code is used up: the same code is now wrong.
    final again = install(_Script(['login-alice']));
    expect(await again.c.signInAndEnroll(recoveryCode: code),
        EnrollResult.failed);
    expect(again.c.state.error, EnrollmentController.recoveryWrongError);
    expect(await again.c.store.read(), isNull);

    // Bob has no device and no code: wrong codes count up to the lockout (429).
    String? last;
    for (var n = 0; n < 6; n++) {
      final i = install(_Script(['login-bob']));
      await i.c.signInAndEnroll(recoveryCode: '0000-0000-0000');
      last = i.c.state.error;
      if (last == EnrollmentController.recoveryLockedError) break;
      expect(last, EnrollmentController.recoveryWrongError);
    }
    expect(last, EnrollmentController.recoveryLockedError);
  }, skip: skip);
}
