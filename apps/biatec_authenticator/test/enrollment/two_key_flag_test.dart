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
      expect(body['kid'], keys.keys!.presence.kid);
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
      expect(keys.presenceSigned, hasLength(1));
      expect((oidc.receipts.single['device'] as Map)['kid'], 'k');
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
