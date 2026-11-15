// Contract test: the real approval client, link controller and receipt signing against a running packages/consent-protocol/mock-oidc server (protocol 1.2, T-246). The relying party's side is the
// protocol kit's own helper (test/links/rp_side.mjs, which uses scripts/rp-client.mjs); the mock checks every statement and receipt with lib.mjs as Biatec OIDC would, and the test verifies what the
// relying party gets with `verifyLinkPinned` and `verifyReceiptPinned` (pinned keys only).
//
//   cd packages/consent-protocol/mock-oidc
//   MOCK_TWO_KEYS=1 MOCK_JSON_GROUPS=1 MOCK_TEST_HELPERS=1 MOCK_MAX_PENDING=1000 PORT=8096 node src/server.ts & pid=$!
//   (cd ../../../apps/biatec_authenticator && flutter test --dart-define=MOCK_OIDC_URL=http://127.0.0.1:8096 test/links/mock_oidc_link_contract_test.dart); kill $pid
//
// Skipped when MOCK_OIDC_URL is not set (and, for the parts that need the relying party's side or the kit, when node is not installed). Uses the seeded user alice.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/links/link_code.dart';
import 'package:biatec_authenticator/links/link_controller.dart';
import 'package:biatec_authenticator/links/link_services.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import '../approvals/approval_test.dart' show FakeGate;
import '../approvals/rp_support.dart' show nodeSkip, runNode, verifiedByKit;
import '../approvals/support.dart';

void main() {
  const url = String.fromEnvironment('MOCK_OIDC_URL');
  final baseUrl = url.isNotEmpty ? url : null;
  final skip = baseUrl == null
      ? 'set MOCK_OIDC_URL to run'
      : nodeSkip ?? false;
  var n = 0;

  /// The relying party's side, one call of rp_side.mjs.
  Future<Map<String, Object?>> side(Map<String, Object?> input) async =>
      (jsonDecode(await runNode('test/links/rp_side.mjs', input)) as Map)
          .cast<String, Object?>();

  /// A new relying party that registered, and (when [prove]) served its well-known file. [rp] is its state for the next call.
  Future<({Map<String, Object?> rp, String fingerprint, String host})> register(
      {bool prove = true}) async {
    final host = 'bank${DateTime.now().microsecondsSinceEpoch}-${n++}.example';
    final r = await side({
      'cmd': 'register',
      'base': baseUrl,
      'issuer': 'https://oidc.biatec.test',
      'host': host,
      'prove': prove,
    });
    return (
      rp: (r['rp'] as Map).cast<String, Object?>(),
      fingerprint: r['fingerprint'] as String,
      host: host
    );
  }

  Future<({int status, Map<String, Object?> json})> call(
      Map<String, Object?> rp, String method, String path,
      {Map<String, Object?>? body, Map<String, Object?>? form}) async {
    final r = await side({
      'cmd': 'call',
      'rp': rp,
      'method': method,
      'path': path,
      if (body != null) 'body': body,
      if (form != null) 'form': form,
    });
    return (
      status: r['status'] as int,
      json: (r['json'] as Map?)?.cast<String, Object?>() ?? const {}
    );
  }

  /// The party starts a link: its link code, the code on its screen, the link id.
  Future<({String uri, String code, String id})> startLink(
      Map<String, Object?> rp,
      {String label = 'Alex M. (account ending 4421)'}) async {
    final r = await call(rp, 'POST', '/consent/links', body: {'user_label': label});
    expect(r.status, 201, reason: '${r.json}');
    return (
      uri: r.json['link_uri'] as String,
      code: r.json['binding_code'] as String,
      id: r.json['link_id'] as String
    );
  }

  /// Alice's phone: her seeded device (the vector device key and presence key), with the services around it.
  Future<
      ({
        SoftwareKeyStore keys,
        LinkServices links,
        ApprovalServices approvals,
        SettingsServices settings,
        MemoryHistoryStore history,
      })> phone({bool mockIssuer = true}) async {
    final d = jsonDecode(
        (await http.get(Uri.parse('$baseUrl/mock/devices/alice-device'))).body) as Map;
    final store = MemoryEnrollmentStore();
    await store.write(EnrollmentRecord(
      kid: 'alice-device',
      deviceToken: d['device_token'] as String,
      activeAt: DateTime.utc(2020),
      attestation: d['attestation'] as String,
      presenceKid: (d['presence'] as Map)['kid'] as String,
      presenceAttestation: (d['presence'] as Map)['attestation'] as String,
    ));
    final keys = SoftwareKeyStore(deviceTestKey());
    final enrollment = ObservableEnrollmentStore(store);
    final history = MemoryHistoryStore();
    final client = ApprovalClient(baseUrl: baseUrl!, relyingParties: true);
    final links = LinkServices(
      client: client,
      keys: keys,
      enrollment: enrollment,
      history: history,
      enrolledOrigin: originOf(baseUrl),
      // The mock's issuer is a fixed name (a debug build against the mock accepts exactly that one).
      extraIssuers: mockIssuer ? const {LinkServices.mockIssuer} : const {},
    );
    return (
      keys: keys,
      links: links,
      approvals: ApprovalServices(
        client: client,
        keys: keys,
        enrollment: enrollment,
        gate: FakeGate(),
        history: history,
        trustSettings: true,
      ),
      settings: SettingsServices(
        client: SettingsClient(baseUrl: baseUrl),
        keys: keys,
        enrollment: enrollment,
        gate: FakeGate(),
        trustSettings: true,
      ),
      history: history,
    );
  }

  Future<List<ClientSetting>> linkedServices(SettingsServices s) async {
    final c = s.controller();
    await c.load();
    expect(c.phase, SettingsPhase.ready, reason: c.error);
    return [for (final x in c.settings!.clients) if (x.linked) x];
  }

  test('the code of the mock names an issuer that is not the server it is served from: refused without the development allowance',
      () async {
    final rp = await register();
    final link = await startLink(rp.rp);
    final real = await phone(mockIssuer: false);
    expect(
        () => real.links.parse(link.uri),
        throwsA(isA<LinkCodeException>().having(
            (e) => e.problem, 'problem', LinkCodeProblem.differentService)));
  }, skip: skip);

  test('approve creates the link: the service is in Linked services, the statement is by the approval key and verifies with lib.mjs',
      () async {
    final rp = await register();
    final link = await startLink(rp.rp);
    final p = await phone();
    final c = p.links.controllerFor(p.links.parse(link.uri));
    await c.load();
    expect(c.phase, LinkPhase.ready, reason: c.unavailableReason);
    expect(c.request!.client.domain, rp.host);
    expect(c.request!.client.domainProven, isTrue);
    expect(c.request!.userLabel, 'Alex M. (account ending 4421)');
    await c.approve(link.code);
    expect(c.phase, LinkPhase.linked, reason: c.error);

    // One prompt, the approval key only.
    expect(p.keys.prompts, hasLength(1));
    expect(p.keys.signed, hasLength(1));
    expect(p.keys.presenceSigned, isEmpty);
    // The service appears in Linked services.
    final linked = await linkedServices(p.settings);
    expect(linked.map((x) => x.domain), contains(rp.host));
    expect((await p.history.read()).single.summary, 'Link a service');

    // The party sees the user's statement and verifies it with the keys of the answer (lib.mjs verifyLinkPinned).
    final status = await call(rp.rp, 'GET', '/consent/links/${link.id}');
    expect(status.status, 200, reason: '${status.json}');
    expect(status.json['status'], 'linked');
    final approval = (status.json['approval'] as Map).cast<String, Object?>();
    final signed = (approval['signed'] as Map).cast<String, Object?>();
    expect(signed['decision'], 'approve');
    expect(signed['domain'], rp.host);
    expect(signed['kid'], 'alice-device');
    final ok = await runNode('test/links/verify_link.mjs', {
      'approval': {'signed': signed, 'signature': approval['signature']},
      'devices': status.json['devices'],
      'expect': {
        'link_id': link.id,
        'client_fingerprint': rp.fingerprint,
        'domain': rp.host,
        'nonce': signed['nonce'],
        'binding_code': link.code,
      },
      'now': DateTime.now().millisecondsSinceEpoch ~/ 1000,
    });
    expect(ok, 'true');
    // The link is single use: the same code again finds nothing to do.
    final again = p.links.controllerFor(p.links.parse(link.uri));
    await again.load();
    expect(again.phase, LinkPhase.gone);
  }, skip: skip);

  test('deny does not link: the party is told it was denied and the service is not in Linked services',
      () async {
    final rp = await register();
    final link = await startLink(rp.rp);
    final p = await phone();
    final c = p.links.controllerFor(p.links.parse(link.uri));
    await c.load();
    await c.deny('');
    expect(c.phase, LinkPhase.denied, reason: c.error);
    // Presence key, no prompt.
    expect(p.keys.prompts, isEmpty);
    expect(p.keys.presenceSigned, hasLength(1));
    final linked = await linkedServices(p.settings);
    expect(linked.map((x) => x.domain), isNot(contains(rp.host)));
    final status = await call(rp.rp, 'GET', '/consent/links/${link.id}');
    expect(status.json['status'], 'denied');
    expect((status.json['approval'] as Map)['signed'], containsPair('decision', 'deny'));
  }, skip: skip);

  test('three wrong codes close the link', () async {
    final rp = await register();
    final link = await startLink(rp.rp);
    final p = await phone();
    final c = p.links.controllerFor(p.links.parse(link.uri));
    await c.load();
    final wrong = link.code == '000000' ? '111111' : '000000';
    await c.approve(wrong);
    expect(c.phase, LinkPhase.ready);
    expect(c.attemptsRemaining, 2, reason: c.error);
    expect(c.error, 'Wrong code. 2 attempts left.');
    await c.approve(wrong);
    expect(c.attemptsRemaining, 1);
    await c.approve(wrong);
    expect(c.phase, LinkPhase.closed, reason: c.error);
    // Even the right code finds the link closed.
    final again = p.links.controllerFor(p.links.parse(link.uri));
    await again.load();
    expect(again.phase, LinkPhase.gone);
    final linked = await linkedServices(p.settings);
    expect(linked.map((x) => x.domain), isNot(contains(rp.host)));
    final status = await call(rp.rp, 'GET', '/consent/links/${link.id}');
    expect(status.json['status'], 'expired');
  }, skip: skip);

  test('a service that has not proven its domain is linked only after the extra confirmation, and the statement says so',
      () async {
    final rp = await register(prove: false);
    final link = await startLink(rp.rp);
    final p = await phone();
    final c = p.links.controllerFor(p.links.parse(link.uri));
    await c.load();
    expect(c.request!.client.domainProven, isFalse);
    await c.approve(link.code);
    expect(c.phase, LinkPhase.ready, reason: 'not without the confirmation');
    await c.approve(link.code, confirmedUnproven: true);
    expect(c.phase, LinkPhase.linked, reason: c.error);
    final status = await call(rp.rp, 'GET', '/consent/links/${link.id}');
    expect(((status.json['approval'] as Map)['signed'] as Map)['domain'], isNull);
  }, skip: skip);

  test('after linking: a payment is approved, a login denied, an action approved; the party verifies each receipt with the pinned keys only',
      () async {
    final rp = await register();
    final link = await startLink(rp.rp);
    final p = await phone();
    final lc = p.links.controllerFor(p.links.parse(link.uri));
    await lc.load();
    await lc.approve(link.code);
    expect(lc.phase, LinkPhase.linked, reason: lc.error);
    final status = (await call(rp.rp, 'GET', '/consent/links/${link.id}')).json;
    final subject = status['subject'] as String;
    final pins = status['devices'] as List;

    Future<({String id, String code, Map<String, Object?> request})> open(
        Map<String, Object?> details) async {
      final origin = 'https://netbanking.${rp.host}';
      final r = await call(rp.rp, 'POST', '/connect/ciba', body: {
        'scope': 'openid',
        'login_hint': subject,
        'authorization_details': [details],
        'origin': origin,
        'requested_expiry': 300,
      });
      expect(r.status, 200, reason: '${r.json}');
      final id = r.json['auth_req_id'] as String;
      return (id: id, code: r.json['binding_code'] as String, request: <String, Object?>{});
    }

    Future<Map<String, Object?>> receiptOf(String id, {bool denied = false}) async {
      final t = await call(rp.rp, 'POST', '/token',
          form: {'grant_type': 'urn:openid:params:grant-type:ciba', 'auth_req_id': id});
      if (denied) {
        expect(t.json['error'], 'access_denied', reason: '${t.json}');
        final g = await call(rp.rp, 'GET', '/consent/receipts/$id');
        return (g.json as Map).cast<String, Object?>();
      }
      expect(t.status, 200, reason: '${t.json}');
      return (t.json['consent_receipt'] as Map).cast<String, Object?>();
    }

    // A payment, approved.
    final payment = {
      'type': 'biatec_payment',
      'instructed_amount': {'amount': '12.50', 'currency': 'EUR'},
      'creditor_name': 'Muster Energie GmbH',
      'creditor_account': {'iban': 'DE89370400440532013000'},
      'remittance': 'Invoice 2026-0042',
      'execution_date': '2026-10-20',
    };
    final pay = await open(payment);
    final pc = p.approvals.controllerFor(pay.id);
    await pc.load();
    expect(pc.phase, ApprovalPhase.ready, reason: pc.error);
    expect(pc.request!.isRelyingParty, isTrue);
    await pc.approve(pay.code);
    expect(pc.phase, ApprovalPhase.approved, reason: pc.error);
    final payReceipt = await receiptOf(pay.id);
    expect(payReceipt['version'], '1.2');
    expect((payReceipt['signed'] as Map)['decision'], 'approve');
    final payRequest = pc.request!.json;
    expect(
        await verifiedByKit(payReceipt, payRequest, pay.code, pinned: pins), isTrue,
        reason: 'verifyReceiptPinned');

    // A login, denied.
    final login = await open({
      'type': 'biatec_login',
      'context': {'ip': '203.0.113.42', 'location': 'Bratislava, Slovakia'},
    });
    final lg = p.approvals.controllerFor(login.id);
    await lg.load();
    expect(lg.phase, ApprovalPhase.ready, reason: lg.error);
    await lg.deny();
    expect(lg.phase, ApprovalPhase.denied, reason: lg.error);
    final loginReceipt = await receiptOf(login.id, denied: true);
    expect((loginReceipt['signed'] as Map)['decision'], 'deny');
    expect(
        await verifiedByKit(loginReceipt, lg.request!.json, login.code,
            decision: 'deny', pinned: pins),
        isTrue);

    // An action, approved.
    final action = await open({
      'type': 'biatec_action',
      'title': 'Add payee',
      'lines': [
        {'label': 'Payee', 'value': 'Muster Energie GmbH'},
        {'label': 'Daily limit', 'value': '5 000 EUR'},
      ],
    });
    final ac = p.approvals.controllerFor(action.id);
    await ac.load();
    await ac.approve(action.code);
    expect(ac.phase, ApprovalPhase.approved, reason: ac.error);
    expect(
        await verifiedByKit(await receiptOf(action.id), ac.request!.json, action.code,
            pinned: pins),
        isTrue);

    // History: the payment with its amount, the login, the action, and the link, with no code in any of them.
    final summaries = [for (final e in await p.history.read()) e.summary];
    expect(summaries, containsAll([
      'Payment: 12.50 EUR to Muster Energie GmbH',
      'Sign in',
      'Action: Add payee',
      'Link a service',
    ]));
  }, skip: skip);
}
