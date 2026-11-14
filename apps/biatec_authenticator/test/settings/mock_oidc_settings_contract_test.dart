// Contract test: the real settings client, controller and approval controller against a running packages/consent-protocol/mock-oidc server, which classifies every
// change itself, verifies the device signature of every loosening and the receipt (with `remember`) as Biatec OIDC would (issues 27 and 30, README "Settings (issue 30, T-245)").
//
//   cd packages/consent-protocol/mock-oidc
//   MOCK_TWO_KEYS=1 MOCK_JSON_GROUPS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 MOCK_MAX_THRESHOLD_EUR=5000 PORT=8094 node src/server.ts & pid=$!
//   (cd ../../../apps/biatec_authenticator && flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8094 --dart-define=MOCK_MAX_THRESHOLD_EUR=5000 test/settings/mock_oidc_settings_contract_test.dart); kill $pid
//
// Skipped when MOCK_OIDC_URL is not set. The cases use the seeded users hank (a linked `biatec-chat`) and dave (a paused `biatec-chat`), and leave hank's settings as they found them.
// The operator ceiling case runs only with MOCK_MAX_THRESHOLD_EUR (the same value the mock was started with).
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../approvals/support.dart';
import 'support.dart';

class _Pass implements BiometricGate {
  @override
  Future<bool> confirm(String reason) async => true;
}

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  const maxThreshold = String.fromEnvironment('MOCK_MAX_THRESHOLD_EUR');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null ? 'set MOCK_OIDC_URL to run' : false;
  const address = 'HANKADDRESS';
  const network = 'algorand-testnet';
  final chat =
      'Basic ${base64.encode(utf8.encode('biatec-chat:biatec-chat-secret'))}';

  /// A seeded user's phone: its device (which signs with the vector device key), the settings and the approval controllers.
  Future<({SettingsController settings, SoftwareKeyStore keys, ApprovalServices approvals})>
      phone(String name) async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/$name-device'))).body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
      kid: '$name-device',
      deviceToken: d['device_token'] as String,
      activeAt: DateTime.utc(2020),
      attestation: d['attestation'] as String,
      presenceKid: (d['presence'] as Map)['kid'] as String,
      presenceAttestation: (d['presence'] as Map)['attestation'] as String,
    ));
    final keys = SoftwareKeyStore(deviceTestKey(),
        approvalKid: '$name-device', presenceKid: '$name-device-presence');
    final enrollment = ObservableEnrollmentStore(store);
    final services = SettingsServices(
      client: SettingsClient(baseUrl: baseUrl!),
      keys: keys,
      enrollment: enrollment,
      gate: FakeGate(),
      trustSettings: true,
    );
    final approvals = ApprovalServices(
      client: ApprovalClient(baseUrl: baseUrl),
      keys: keys,
      enrollment: enrollment,
      gate: _Pass(),
      history: MemoryHistoryStore(),
      trustSettings: true,
    );
    final c = services.controller();
    await c.load();
    expect(c.phase, SettingsPhase.ready, reason: c.error);
    return (settings: c, keys: keys, approvals: approvals);
  }

  String tx(String fiat, {int? app, String note = ''}) =>
      base64.encode(utf8.encode(jsonEncode({
        'sender': address,
        'receiver': 'PAYEE',
        if (app != null) 'application_id': app,
        'asset': {'id': '31566704', 'symbol': 'USDC', 'decimals': 6},
        'amount': '$fiat.000000',
        'fiat_eur': '$fiat.00',
      })));

  /// A signing call of hank's chat client with a bearer token: 200 when signed without a request, 401 when the phone is asked.
  Future<http.Response> sign(String item) => http.post(
      Uri.parse('$baseUrl/wallet/$network/$address/sign'),
      headers: {
        'Authorization': 'Bearer hank-chat-sign',
        'Content-Type': 'application/json'
      },
      body: jsonEncode({
        'transactions': [item],
        'origin': 'https://chat.biatec.io'
      }));

  /// Opens a direct-entry request for hank (the phone is asked whatever the value), as a client would.
  Future<({String id, String code})> openRequest(String item) async {
    final res = await http.post(Uri.parse('$baseUrl/connect/ciba'),
        headers: {'Authorization': chat, 'Content-Type': 'application/json'},
        body: jsonEncode({
          'scope': 'sign',
          'resource': 'https://api.biatec.io',
          'login_hint': 'hank@example.com',
          'network': network,
          'address': address,
          'unsigned_group': [item],
          'origin': 'https://chat.biatec.io',
        }));
    expect(res.statusCode, 200, reason: res.body);
    final json = jsonDecode(res.body) as Map;
    return (
      id: json['auth_req_id'] as String,
      code: json['binding_code'] as String
    );
  }

  Future<Map> consentSettings() async => jsonDecode((await http.get(
          Uri.parse('$baseUrl/consent/settings'),
          headers: {'Authorization': 'Bearer hank-chat-sign'}))
      .body) as Map;

  test('a threshold of the user\'s own: lowered at once, raised with one signature, restored',
      () async {
    final p = await phone('hank');
    final c = p.settings;
    expect(c.settings!.thresholdEur, '1000.00');
    // 2000 EUR is above the threshold: the phone is asked.
    expect((await sign(tx('2000', note: 'a'))).statusCode, 401);

    expect(await c.apply({'threshold_eur': '5000'}), isTrue, reason: c.error);
    expect(p.keys.signed, hasLength(1), reason: 'raising signs exactly once');
    expect(p.keys.prompts, hasLength(1));
    expect(c.settings!.thresholdEur, '5000.00');
    // With the threshold at 5000 a 2000 EUR transfer is signed without a request.
    final res = await sign(tx('2000', note: 'b'));
    expect(res.statusCode, 200, reason: res.body);

    expect(await c.apply({'threshold_eur': '1000'}), isTrue, reason: c.error);
    expect(p.keys.signed, hasLength(1), reason: 'lowering signs nothing');
    expect(c.settings!.thresholdEur, '1000.00');
    expect((await sign(tx('2000', note: 'c'))).statusCode, 401);
  }, skip: skip);

  test('0 asks for everything', () async {
    final p = await phone('hank');
    final c = p.settings;
    expect(await c.apply({'threshold_eur': '0'}), isTrue, reason: c.error);
    expect((await sign(tx('1', note: 'z'))).statusCode, 401);
    expect(await c.apply({'threshold_eur': '1000'}), isTrue,
        reason: c.error);
    expect(c.settings!.thresholdEur, '1000.00');
  }, skip: skip);

  test('above the operator maximum: the server\'s refusal, no signature retry', () async {
    final p = await phone('hank');
    final c = p.settings;
    expect(await c.apply({'threshold_eur': '9000'}), isFalse);
    expect(c.error, contains('threshold_eur must not be more than 5000.00 EUR'));
    expect(p.keys.signed, isEmpty);
    expect(c.settings!.thresholdEur, '1000.00');
  },
      skip: skip != false || maxThreshold.isEmpty
          ? 'set MOCK_OIDC_URL and MOCK_MAX_THRESHOLD_EUR (the mock\'s own) to run'
          : false);

  test(
      'approving with remember: the same application under the threshold is signed without a request; removing it asks again',
      () async {
    final p = await phone('hank');
    final item = tx('10', app: 777, note: 'r1');
    // An untrusted application makes the value unknown: the phone is asked.
    expect((await sign(item)).statusCode, 401);
    final r = await openRequest(tx('10', app: 777, note: 'r2'));
    final ctl = p.approvals.controllerFor(r.id);
    await ctl.load();
    expect(ctl.phase, ApprovalPhase.ready, reason: ctl.error);
    expect(ctl.request!.client.linked, isTrue);
    expect(ctl.request!.rememberable.map((a) => a.id), ['777']);
    ctl.setRemember('777', true);
    await ctl.approve(r.code);
    expect(ctl.phase, ApprovalPhase.approved, reason: ctl.error);
    // The mock verified the receipt, `remember` included, with the protocol kit.
    final apps = (await consentSettings())['client']['trusted_applications'] as List;
    expect(apps, [
      {'network': network, 'app_id': 777}
    ]);
    final res = await sign(tx('10', app: 777, note: 'r3'));
    expect(res.statusCode, 200, reason: res.body);

    await p.settings.load();
    final mine = p.settings.settings!.client('biatec-chat')!;
    expect(mine.trustedApplications.map((a) => a.appId), [777]);
    expect(
        await p.settings.apply({
          'clients': {
            'biatec-chat': {
              'trusted_applications_remove': [mine.trustedApplications.single.toJson()]
            }
          }
        }),
        isTrue,
        reason: p.settings.error);
    expect(p.keys.signed, hasLength(1), reason: 'only the approval signed; removing is tightening');
    expect((await sign(tx('10', app: 777, note: 'r4'))).statusCode, 401);
  }, skip: skip);

  test('label (tightening), always ask off (signed), the server\'s text rules', () async {
    final p = await phone('hank');
    final c = p.settings;
    expect(
        await c.apply({
          'clients': {
            'biatec-chat': {'label': 'My chat'}
          }
        }),
        isTrue,
        reason: c.error);
    expect(c.settings!.client('biatec-chat')!.label, 'My chat');
    expect(p.keys.signed, isEmpty);
    // The server refuses a right-to-left override that the app would also have refused.
    expect(
        await c.apply({
          'clients': {
            'biatec-chat': {'label': 'Bank\u202E'}
          }
        }),
        isFalse);
    expect(c.error, contains('400'));
    expect(await c.apply({'clients': {'biatec-chat': {'always_ask': true}}}), isTrue);
    expect(p.keys.signed, isEmpty);
    expect(await c.apply({'clients': {'biatec-chat': {'always_ask': false}}}), isTrue,
        reason: c.error);
    expect(p.keys.signed, hasLength(1));
  }, skip: skip);

  test('a pause is lifted with a signature over the pause\'s own origin', () async {
    final p = await phone('dave');
    final client = p.settings.settings!.client('biatec-chat')!;
    expect(client.pausedUntil, isNotNull);
    expect(client.pauses, isNotEmpty);
    expect(
        await p.settings.apply({
          'clients': {
            'biatec-chat': {'paused': false, 'origin': client.pauses.first.origin}
          }
        }),
        isTrue,
        reason: p.settings.error);
    expect(p.keys.signed, hasLength(1));
    expect(p.settings.settings!.client('biatec-chat')!.pausedUntil, isNull);
  }, skip: skip);

  test('unlink: the next request is first use again, and the service is linked again by approving',
      () async {
    final p = await phone('hank');
    final c = p.settings;
    expect(await c.apply({'clients': {'biatec-chat': {'unlink': true}}}), isTrue,
        reason: c.error);
    expect(p.keys.signed, isEmpty, reason: 'unlinking is tightening');
    expect(c.settings!.client('biatec-chat')?.linkedAt, isNull);
    final s = await consentSettings();
    expect(s['client']['first_use'], isTrue);
    expect((await sign(tx('5', note: 'u1'))).statusCode, 401);

    // The request now says "New service"; approving links it again.
    final r = await openRequest(tx('5', note: 'u2'));
    final ctl = p.approvals.controllerFor(r.id);
    await ctl.load();
    expect(ctl.request!.client.linked, isFalse);
    await ctl.approve(r.code);
    expect(ctl.phase, ApprovalPhase.approved, reason: ctl.error);
    expect((await consentSettings())['client']['first_use'], isFalse);
  }, skip: skip);

  test('an inactive device reads the settings and cannot change them', () async {
    // A second phone enrolls with two keys; it is inactive for 24 hours.
    final other = EnrollmentController(
      keys: randomSoftwareKeyStore(),
      store: ObservableEnrollmentStore(MemoryEnrollmentStore()),
      client: OidcEnrollmentClient(baseUrl: baseUrl!),
    );
    await other.enroll(loginToken: 'login-alice');
    expect(other.state.phase, EnrollmentPhase.inactive, reason: other.state.error);
    final record = other.state.record!;
    final client = SettingsClient(baseUrl: baseUrl);
    final read = await client.settings(record.deviceToken);
    expect(read.clients, isNotEmpty);
    await expectLater(
        client.change(record.deviceToken, {'threshold_eur': '10'}),
        throwsA(isA<ApprovalException>()));
  }, skip: skip);
}
