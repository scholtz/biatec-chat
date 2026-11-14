// T-241: the two-key flow is switched by configuration (`AppConfig.twoKeys`). Against a Biatec OIDC without the two keys (the real one, for now) the
// app enrolls ONE key and sends no second key, no proofs and no challenge it does not need.
import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/receipt.dart';
import 'package:biatec_authenticator/security/device_proof.dart';
import 'package:biatec_authenticator/config.dart';
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

import '../approvals/approval_test.dart'
    show vectorRequest, vectorNow, FakeOidc, FakeGate;
import '../approvals/support.dart';
import '../keys/fake_two_keys.dart';

class _NoAttestation implements AttestationProvider {
  @override
  Future<bool> supported() async => false;
  @override
  Future<AttestationToken?> token(String nonce) async => null;
}

void main() {
  final t0 = DateTime.utc(2026, 11, 1, 9);

  group('configuration', () {
    test('the mock has the two keys; a real URL does not until it is said',
        () {
      expect(AppConfig.resolve('').twoKeys, isTrue);
      expect(AppConfig.resolve(AppConfig.mockOidcBaseUrl).twoKeys, isTrue);
      expect(
          AppConfig.resolve('https://oidc.example', release: true).twoKeys,
          isFalse);
      expect(
          AppConfig.resolve('https://oidc.example',
                  twoKeys: true, release: true)
              .twoKeys,
          isTrue);
    });
  });

  group('enrollment with and without the two keys', () {
    late List<http.Request> requests;
    late FakeTwoKeyStore keys;

    EnrollmentController build(
        {required bool twoKeys, EnrollmentStore? store}) {
      final client = MockClient((r) async {
        requests.add(r);
        if (r.url.path == '/devices/challenge') {
          return http.Response(jsonEncode({'nonce': 'n' * 32}), 200);
        }
        final body = jsonDecode(r.body) as Map;
        return http.Response(
            jsonEncode({
              'kid': body['kid'],
              'active_at':
                  t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/
                      1000,
              'device_token': 'tok',
              'attestation': 'a.b.c',
              if (twoKeys) ...{
                'presence_kid': body['presence_kid'],
                'presence_attestation': 'p.b.c'
              },
            }),
            201);
      });
      return EnrollmentController(
        keys: keys,
        store: ObservableEnrollmentStore(store ?? MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
        clock: () => t0,
        attestation: _NoAttestation(),
        twoKeys: twoKeys,
      );
    }

    setUp(() {
      requests = [];
      keys = FakeTwoKeyStore();
    });

    test(
        'one key, no second key, no proofs, and no challenge the platform does not need',
        () async {
      final c = build(twoKeys: false);
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.inactive, reason: c.state.error);
      expect(
          requests.where((r) => r.url.path == '/devices/challenge'), isEmpty);
      final body = jsonDecode(requests.single.body) as Map;
      expect(body.keys.toSet(), {'kid', 'public_key'});
      // The enrolled key is the prompt-free one: nothing in this mode asks the OS for a biometric.
      expect(body['kid'], (await keys.loadPresence())!.kid);
      // ONLY the prompt-free key was made: no approval key, no biometric.
      expect(keys.presenceOnlyGenerated, 1);
      expect(keys.generated, isEmpty);
      expect(keys.prompts, 0);
      expect(c.state.record!.presenceKid, isNull);
      // A one-key record is normal in this mode, not a legacy install.
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.inactive);
      expect(c.keyLevel, isNotNull);
    });

    test('two keys: the challenge, both keys and both proofs', () async {
      final c = build(twoKeys: true);
      await c.enroll(loginToken: 'login-alice');
      final body = jsonDecode(requests.last.body) as Map;
      expect(requests.first.url.path, '/devices/challenge');
      expect(
          body.keys.toSet(),
          containsAll([
            'presence_kid',
            'presence_public_key',
            'approval_proof',
            'presence_proof'
          ]));
      expect(body['kid'], keys.keys!.approval.kid);
    });

    test(
        'a failed enrollment keeps its error when a one-key record is found (two-key mode)',
        () async {
      final store = MemoryEnrollmentStore();
      final c = build(twoKeys: true, store: store);
      keys.failGenerate = true;
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.failed);
      final error = c.state.error;
      await store.write(EnrollmentRecord(
          kid: 'old',
          deviceToken: 't',
          activeAt: DateTime.utc(2020),
          attestation: 'a'));
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(c.state.error, error, reason: 'not replaced by the legacy notice');
    });

    test('a key generation failure has its own text', () async {
      final c = build(twoKeys: true);
      keys.generationFailed = true;
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.error, contains('could not create the device keys'));
      expect(c.state.error, isNot(contains('fingerprint')));
    });
  });

  group('approving without the two keys', () {
    test('the app gate asks, the prompt-free key signs, no OS prompt',
        () async {
      final oidc = FakeOidc(vectorRequest('stepup-transaction-approve'));
      final keys =
          SoftwareKeyStore(deviceTestKey(), approvalKid: 'k', presenceKid: 'k');
      final store = MemoryEnrollmentStore()
        ..write(EnrollmentRecord(
            kid: 'k',
            deviceToken: 'dev-token',
            activeAt: DateTime.utc(2020),
            attestation: 'h.p.s'));
      final gate = FakeGate();
      final services = ApprovalServices(
        client: ApprovalClient(baseUrl: 'http://oidc', client: oidc.client),
        keys: keys,
        enrollment: ObservableEnrollmentStore(store),
        gate: gate,
        history: MemoryHistoryStore(),
        twoKeys: false,
        clock: vectorNow,
      );
      final c = services.controllerFor(oidc.request['auth_req_id'] as String);
      await c.load();
      await c.approve('483920');
      expect(oidc.receipts, hasLength(1), reason: c.error);
      expect(gate.asked, 1);
      expect(keys.prompts, isEmpty);
      expect(keys.legacySigned, hasLength(1),
          reason: 'signed with the old alias');
      expect(keys.presenceSigned, isEmpty);
      expect((oidc.receipts.single['device'] as Map)['kid'], 'k');
    });

    test('a single-key record without an attestation refuses deny too',
        () async {
      final oidc = FakeOidc(vectorRequest('stepup-transaction-approve'));
      final keys =
          SoftwareKeyStore(deviceTestKey(), approvalKid: 'k', presenceKid: 'k');
      final store = MemoryEnrollmentStore()
        ..write(EnrollmentRecord(
            kid: 'k', deviceToken: 'dev-token', activeAt: DateTime.utc(2020)));
      final services = ApprovalServices(
        client: ApprovalClient(baseUrl: 'http://oidc', client: oidc.client),
        keys: keys,
        enrollment: ObservableEnrollmentStore(store),
        gate: FakeGate(),
        history: MemoryHistoryStore(),
        twoKeys: false,
        clock: vectorNow,
      );
      final c = services.controllerFor(oidc.request['auth_req_id'] as String);
      await c.load();
      await c.deny();
      expect(c.error,
          'This enrollment has no attestation. Enroll the device again.');
      expect(oidc.receipts, isEmpty);
    });
  });

  group('upgrade from a build before the two keys', () {
    final old = EnrollmentRecord(
        kid: 'biatec-authenticator-abc',
        deviceToken: 'tok',
        activeAt: DateTime.utc(2020),
        attestation: 'a.b.c');

    EnrollmentController controller(FakeTwoKeyStore keys,
            {required bool twoKeys}) =>
        EnrollmentController(
          keys: keys,
          store:
              ObservableEnrollmentStore(MemoryEnrollmentStore()..write(old)),
          client: OidcEnrollmentClient(
              baseUrl: 'http://oidc',
              client: MockClient((r) async => http.Response('{}', 404))),
          clock: () => t0,
          attestation: _NoAttestation(),
          twoKeys: twoKeys,
        );

    test(
        'single-key mode: the old record stays valid and its key is found under the old alias',
        () async {
      final keys = FakeTwoKeyStore()
        ..legacy['biatec-authenticator-abc'] = const DeviceKey(
            kid: 'biatec-authenticator-abc',
            x: 'x',
            y: 'y',
            securityLevel: KeySecurityLevel.trustedEnvironment);
      final c = controller(keys, twoKeys: false);
      await c.refresh();
      expect(c.state.phase, isNot(EnrollmentPhase.notEnrolled));
      expect(c.state.record!.kid, 'biatec-authenticator-abc');
      expect(c.keyLevel, KeySecurityLevel.trustedEnvironment);
    });

    test('two-key mode: the same record asks to enroll again', () async {
      final c = controller(FakeTwoKeyStore(), twoKeys: true);
      await c.refresh();
      expect(c.state.phase, EnrollmentPhase.notEnrolled);
      expect(c.state.error, EnrollmentController.legacyKeyNotice);
    });

    test('the device proof signs with the old alias and names it as the kid',
        () async {
      final keys = FakeTwoKeyStore();
      final signer = KeystoreDeviceProofSigner(
          keys: keys,
          enrollment: MemoryEnrollmentStore()..write(old),
          clock: () => t0);
      final h = await signer.headers(
          'GET', Uri.parse('https://oidc.example/devices'), 'tok');
      final header = jsonDecode(utf8.decode(base64Url
          .decode(base64Url.normalize(h.values.single.split('.')[0])))) as Map;
      expect(header['kid'], 'biatec-authenticator-abc');
      expect(keys.legacySigned, ['biatec-authenticator-abc']);
      expect(keys.presenceSigns, 0);
    });
  });

  group('the enrollment challenge may expire while the user or the platform waits',
      () {
    http.Response created(http.Request r) {
      final body = jsonDecode(r.body) as Map;
      return http.Response(
          jsonEncode({
            'kid': body['kid'],
            'active_at':
                t0.add(const Duration(hours: 24)).millisecondsSinceEpoch ~/
                    1000,
            'device_token': 'tok',
            'attestation': 'a',
            'presence_kid': body['presence_kid'],
            'presence_attestation': 'p',
          }),
          201);
    }

    test('a slow platform: a fresh challenge is fetched before the proofs are made',
        () async {
      var now = t0;
      var challenges = 0;
      final keys = FakeTwoKeyStore();
      final client = MockClient((r) async {
        if (r.url.path == '/devices/challenge') {
          challenges++;
          return http.Response(jsonEncode({'nonce': 'n' * 32}), 200);
        }
        return created(r);
      });
      final c = EnrollmentController(
        keys: keys,
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
        clock: () => now,
        attestation:
            _SlowPlatform(() => now = now.add(const Duration(seconds: 400))),
      );
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.inactive, reason: c.state.error);
      expect(challenges, 2);
      expect(keys.prompts, 1,
          reason: 'the proofs were made once, with the fresh challenge');
    });

    test('the server says the challenge expired: one more try with a fresh one',
        () async {
      var challenges = 0;
      var posts = 0;
      final keys = FakeTwoKeyStore();
      final client = MockClient((r) async {
        if (r.url.path == '/devices/challenge') {
          challenges++;
          return http.Response(jsonEncode({'nonce': 'n' * 32}), 200);
        }
        posts++;
        if (posts == 1) {
          return http.Response(
              jsonEncode({
                'error': 'invalid_challenge',
                'error_description': 'the enrollment challenge is expired'
              }),
              400);
        }
        return created(r);
      });
      final c = EnrollmentController(
        keys: keys,
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
        clock: () => t0,
        attestation: _NoAttestation(),
      );
      final notices = <String?>[];
      c.addListener(() => notices.add(c.state.notice));
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.inactive, reason: c.state.error);
      expect(challenges, 2);
      expect(posts, 2);
      expect(keys.prompts, 2);
      expect(notices, contains('Confirming again...'),
          reason: 'the user is told before the biometric is asked again');
    });

    test('an error with the same words but another code is not retried', () async {
      var posts = 0;
      final keys = FakeTwoKeyStore();
      final client = MockClient((r) async {
        if (r.url.path == '/devices/challenge') {
          return http.Response(jsonEncode({'nonce': 'n' * 32}), 200);
        }
        posts++;
        return http.Response(
            jsonEncode({
              'error': 'conflict',
              'error_description': 'the challenge proof of this kid is taken'
            }),
            409);
      });
      final c = EnrollmentController(
        keys: keys,
        store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
        client: OidcEnrollmentClient(baseUrl: 'http://oidc', client: client),
        clock: () => t0,
        attestation: _NoAttestation(),
      );
      await c.enroll(loginToken: 'login-alice');
      expect(c.state.phase, EnrollmentPhase.failed);
      expect(posts, 1);
      expect(keys.prompts, 1);
    });
  });

  test('issued_at is the time after the approval prompt, not before it', () async {
    var now = t0;
    final keys = _SlowPrompt(() => now = now.add(const Duration(seconds: 40)));
    final request = ConsentRequest.fromJson(vectorRequest('stepup-transaction-approve'));
    final receipt = await signReceipt(
      keys: keys,
      device: DeviceIdentity(
          approvalKey: (await keys.loadApproval())!,
          approvalAttestation: 'a',
          presenceKey: (await keys.loadPresence())!,
          presenceAttestation: 'p'),
      request: request,
      code: '483920',
      decision: Decision.approve,
      now: now,
      clock: () => now,
      prompt: const ApprovalPrompt(title: 't', subtitle: 's'),
    );
    expect(receipt['issued_at'], t0.add(const Duration(seconds: 40)).millisecondsSinceEpoch ~/ 1000);
  });

  test('the device proof does not decode the enrollment record on every call', () async {
    final store = _CountingStore()
      ..write(EnrollmentRecord(
          kid: 'a', deviceToken: 'tok', activeAt: DateTime.utc(2020), presenceKid: 'p', presenceAttestation: 'x'));
    final signer = KeystoreDeviceProofSigner(
        keys: SoftwareKeyStore(deviceTestKey()), enrollment: store, clock: () => t0);
    for (var i = 0; i < 3; i++) {
      await signer.headers('GET', Uri.parse('https://oidc.example/devices'), 'tok');
    }
    expect(store.reads, 1);
  });
}

class _SlowPlatform implements AttestationProvider {
  _SlowPlatform(this.onToken);
  final void Function() onToken;
  @override
  Future<bool> supported() async => true;
  @override
  Future<AttestationToken?> token(String nonce) async {
    onToken();
    return const AttestationToken(kind: 'app_attest', token: 't');
  }
}

class _SlowPrompt extends SoftwareKeyStore {
  _SlowPrompt(this.onPrompt) : super(deviceTestKey());
  final void Function() onPrompt;
  @override
  Future<Uint8List> signApproval(Uint8List data,
      {required String promptTitle, required String promptSubtitle}) {
    onPrompt();
    return super.signApproval(data, promptTitle: promptTitle, promptSubtitle: promptSubtitle);
  }
}

class _CountingStore extends MemoryEnrollmentStore {
  int reads = 0;
  @override
  Future<EnrollmentRecord?> read() {
    reads++;
    return super.read();
  }
}
