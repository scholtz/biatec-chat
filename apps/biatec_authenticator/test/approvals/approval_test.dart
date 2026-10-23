import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/approvals/receipt.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'support.dart';

class FakeGate implements BiometricGate {
  bool pass = true;
  int asked = 0;
  @override
  Future<bool> confirm(String reason) async {
    asked++;
    return pass;
  }
}

/// A scripted Biatec OIDC: serves [request] and answers receipts like the
/// mock (three attempts, code_failed, deny).
class FakeOidc {
  FakeOidc(this.request, {this.code = '483920'});

  Map<String, Object?> request;
  final String code;
  int attempts = 0;
  String status = 'pending';
  bool offline = false;

  /// Records the receipt but loses the answer, like a dropped connection.
  bool dropAnswers = false;
  final List<Map<String, Object?>> receipts = [];

  http.Client get client => MockClient((req) async {
        if (offline) throw http.ClientException('network is unreachable');
        if (req.headers['Authorization'] != 'Device dev-token') {
          return http.Response('{"error":"invalid_token"}', 401);
        }
        final id = request['auth_req_id'];
        if (req.method == 'GET' && req.url.path == '/devices/requests') {
          return http.Response(
              jsonEncode({
                'requests': status == 'pending' ? [request] : []
              }),
              200);
        }
        if (req.method == 'GET' && req.url.path == '/consent/requests/$id') {
          return http.Response(jsonEncode(request), 200);
        }
        if (req.method == 'POST' &&
            req.url.path == '/consent/requests/$id/receipt') {
          final r = (jsonDecode(req.body) as Map).cast<String, Object?>();
          receipts.add(r);
          if (dropAnswers) throw http.ClientException('connection reset');
          final s = (r['signed'] as Map).cast<String, Object?>();
          Response out(int st, Map<String, Object?> b) =>
              http.Response(jsonEncode(b), st);
          // Like the mock: after code_failed only the code_failed receipt.
          if (status == 'code_failed' && s['decision'] != 'code_failed') {
            return out(
                403, {'error': 'access_denied', 'reason': 'code_failed'});
          }
          switch (s['decision']) {
            case 'deny':
              status = 'denied';
              return out(200, {'status': 'denied'});
            case 'code_failed':
              return status == 'code_failed'
                  ? out(200, {'status': 'code_failed'})
                  : out(400, {'error': 'invalid_receipt'});
          }
          if (status == 'code_failed') {
            return out(
                403, {'error': 'access_denied', 'reason': 'code_failed'});
          }
          if (s['binding_code'] != code) {
            attempts++;
            if (attempts >= 3) {
              status = 'code_failed';
              return out(403, {
                'error': 'access_denied',
                'reason': 'code_failed',
                'attempts_remaining': 0
              });
            }
            return out(400,
                {'error': 'wrong_code', 'attempts_remaining': 3 - attempts});
          }
          status = 'approved';
          return out(200, {'status': 'approved'});
        }
        return http.Response('{"error":"not_found"}', 404);
      });
}

typedef Response = http.Response;

Map<String, Object?> firstVector() => vectors('receipts.json').first;

Map<String, Object?> vectorRequest(String name) =>
    (vectors('receipts.json').firstWhere((v) => v['name'] == name)['request']
            as Map)
        .cast<String, Object?>();

/// A clock well before the vectors' expiry (1792425900).
DateTime vectorNow() =>
    DateTime.fromMillisecondsSinceEpoch(1792425620 * 1000, isUtc: true);

class Harness {
  Harness(Map<String, Object?> request,
      {DateTime? activeAt, String? attestation = 'h.p.s'}) {
    oidc = FakeOidc(request);
    keys = SoftwareKeyStore(deviceTestKey());
    store = MemoryEnrollmentStore()
      ..write(EnrollmentRecord(
        kid: 'alice-device',
        deviceToken: 'dev-token',
        activeAt: activeAt ?? DateTime.utc(2020),
        attestation: attestation,
      ));
    services = ApprovalServices(
      client: ApprovalClient(baseUrl: 'http://oidc', client: oidc.client),
      keys: keys,
      enrollment: store,
      gate: gate,
      history: history,
      clock: () => now,
    );
  }

  late final FakeOidc oidc;
  late final SoftwareKeyStore keys;
  late final MemoryEnrollmentStore store;
  final gate = FakeGate();
  final history = MemoryHistoryStore();
  late final ApprovalServices services;
  DateTime now = vectorNow();

  Future<ApprovalController> open() async {
    final c = services.controllerFor(oidc.request['auth_req_id'] as String);
    await c.load();
    return c;
  }
}

void main() {
  group('RFC 8785', () {
    for (final v in vectors('canonicalization.json')) {
      test(v["name"] as String, () {
        expect(canonicalize(v['input']), v['canonical']);
      });
    }

    test('rejects lone surrogates and non-finite numbers', () {
      expect(() => canonicalize('\uD800'), throwsArgumentError);
      expect(() => canonicalize(double.nan), throwsArgumentError);
      expect(() => canonicalize(9007199254740992), throwsArgumentError);
    });
  });

  group('receipt', () {
    for (final v in vectors('receipts.json')) {
      final signed =
          (((v['receipt'] as Map)['signed']) as Map).cast<String, Object?>();
      test('${v['name']}: rebuilds the signed bytes of the vector', () async {
        final request = ConsentRequest.fromJson(
            (v['request'] as Map).cast<String, Object?>());
        final built = signedObject(request,
            code: signed['binding_code'] as String,
            decision: Decision.fromWire(signed['decision'])!);
        expect(canonicalize(built), v['canonical']);
        // The vector's own device signature verifies over our bytes.
        final receipt = (v['receipt'] as Map).cast<String, Object?>();
        final device = (receipt['device'] as Map).cast<String, Object?>();
        expect(
            verifyEs256(
                (device['public_key'] as Map).cast<String, Object?>(),
                canonicalBytes(built),
                base64Url.decode(
                    base64Url.normalize(receipt['signature'] as String))),
            isTrue);
      });
    }

    test('signs with the device key into the receipt envelope', () async {
      final key = deviceTestKey();
      final keys = SoftwareKeyStore(key);
      final request = ConsentRequest.fromJson(
          (firstVector()['request'] as Map).cast<String, Object?>());
      final receipt = await signReceipt(
        keys: keys,
        device: DeviceIdentity(
            key: (await keys.load('alice-device'))!, attestation: 'h.p.s'),
        request: request,
        code: '483920',
        decision: Decision.approve,
        now: vectorNow(),
      );
      expect(receipt['version'], '1');
      expect(receipt['alg'], 'ES256');
      expect(receipt['issued_at'], 1792425620);
      final sig = receipt['signature'] as String;
      expect(sig, matches(RegExp(r'^[A-Za-z0-9_-]{86}$')));
      expect(
          verifyEs256(key, canonicalBytes(receipt['signed']),
              base64Url.decode(base64Url.normalize(sig))),
          isTrue);
      expect((receipt['device'] as Map)['kid'], 'alice-device');
      expect((receipt['device'] as Map)['attestation'], 'h.p.s');
    });

    test('an unverified client binding message is never signed', () {
      final json = vectorRequest('direct-transaction-approve-unverified');
      final request = ConsentRequest.fromJson(
          {...json, 'binding_message': 'Send to me instead'});
      expect(
          signedObject(request,
              code: '111111', decision: Decision.approve)['binding_message'],
          '');
    });
  });

  group('consent request', () {
    test('rejects a bound hash that differs from the detail', () {
      expect(
          () => ConsentRequest.fromJson({
                ...vectorRequest('stepup-transaction-approve'),
                'bound_hash': 'X'
              }),
          throwsFormatException);
    });

    test('a string the receipt cannot sign is rejected', () {
      final json = vectorRequest('stepup-transaction-approve');
      expect(() => ConsentRequest.fromJson({...json, 'origin': '\uD800'}),
          throwsFormatException);
    });

    test('an unverified client never gets a display name', () {
      final json = vectorRequest('direct-transaction-approve-unverified');
      final client = Map<String, Object?>.from(json['client'] as Map)
        ..['display_name'] = 'Your Bank';
      final r = ConsentRequest.fromJson({...json, 'client': client});
      expect(r.client.displayName, isNull);
      expect(clientLabel(r.client), startsWith('Unverified application'));
    });
  });

  group('controller', () {
    test('approve posts a signed receipt and records it', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      expect(c.phase, ApprovalPhase.ready);
      await c.approve('483920');
      expect(c.phase, ApprovalPhase.approved, reason: c.error);
      expect(h.gate.asked, 1);
      final r = h.oidc.receipts.single;
      final s = (r['signed'] as Map).cast<String, Object?>();
      expect(s['decision'], 'approve');
      expect(s['binding_code'], '483920');
      expect(canonicalize(s['authorization_details']),
          canonicalize(h.oidc.request['authorization_details']));
      expect(
          verifyEs256(deviceTestKey(), canonicalBytes(s),
              base64Url.decode(base64Url.normalize(r['signature'] as String))),
          isTrue);
      final hist = await h.history.read();
      expect(hist.single.decision, 'approve');
      // The history keeps the signed receipt without the one-time binding code the user typed, and says so.
      final kept = hist.single.receipt;
      expect((kept['signed'] as Map).containsKey('binding_code'), isFalse);
      expect(kept['redacted'], ['signed.binding_code']);
      expect(kept['signature'], r['signature']);
      expect({...(kept['signed'] as Map), 'binding_code': s['binding_code']}, s);
      expect(jsonEncode(kept), isNot(contains(s['binding_code'] as String)));
    });

    test('third wrong code yields code_failed and a signed code_failed receipt',
        () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      await c.approve('000001');
      expect(c.phase, ApprovalPhase.ready);
      expect(c.attemptsRemaining, 2);
      expect(c.error, contains('2 attempts left'));
      await c.approve('000002');
      expect(c.attemptsRemaining, 1);
      await c.approve('000003');
      expect(c.phase, ApprovalPhase.codeFailed, reason: c.error);
      expect(c.attemptsRemaining, 0);
      final last = (h.oidc.receipts.last['signed'] as Map);
      expect(last['decision'], 'code_failed');
      expect(last['binding_code'], '000003');
      expect(h.oidc.receipts, hasLength(4));
      expect((await h.history.read()).single.decision, 'code_failed');
    });

    test('offline: nothing is signed or approved', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      h.oidc.offline = true;
      await c.approve('483920');
      expect(c.phase, ApprovalPhase.ready);
      expect(c.offline, isTrue);
      expect(h.keys.signed, isEmpty);
      expect(h.oidc.receipts, isEmpty);
      expect(await h.history.read(), isEmpty);
    });

    test('offline at open shows no card', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      h.oidc.offline = true;
      final c = await h.open();
      expect(c.phase, ApprovalPhase.unavailable);
      expect(c.offline, isTrue);
      expect(c.request, isNull);
    });

    test('a request that changed since it was shown is not approved', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      final details =
          jsonDecode(jsonEncode(h.oidc.request['authorization_details']))
              as List;
      (details.single as Map)['amount'] = '9000.000000';
      h.oidc.request = {...h.oidc.request, 'authorization_details': details};
      await c.approve('483920');
      expect(c.phase, ApprovalPhase.ready);
      expect(c.error, contains('changed'));
      expect(h.keys.signed, isEmpty);
      expect((c.request!.detail as TransactionDetail).amount, '9000.000000');
    });

    test('failed biometric check signs nothing', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      h.gate.pass = false;
      final c = await h.open();
      await c.approve('483920');
      expect(c.phase, ApprovalPhase.ready);
      expect(h.keys.signed, isEmpty);
      expect(h.oidc.receipts, isEmpty);
    });

    test('a malformed code is not sent', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      await c.approve('12');
      expect(c.error, contains('six-digit'));
      expect(h.gate.asked, 0);
      expect(h.oidc.receipts, isEmpty);
    });

    test('an inactive device cannot approve', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'),
          activeAt: DateTime.utc(2099));
      final c = await h.open();
      await c.approve('483920');
      expect(c.error, contains('not active'));
      expect(h.oidc.receipts, isEmpty);
    });

    test('an expired request cannot be approved', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      h.now = DateTime.utc(2099);
      final c = await h.open();
      expect(c.phase, ApprovalPhase.expired);
      await c.approve('483920');
      expect(h.oidc.receipts, isEmpty);
    });

    test('a lost answer to a posted receipt is not reported as offline',
        () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      h.oidc.dropAnswers = true;
      await c.approve('483920');
      expect(h.oidc.receipts, hasLength(1));
      expect(c.offline, isFalse);
      expect(c.error, contains('may or may not be recorded'));
    });

    test('closing the screen mid-request does not throw', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c =
          h.services.controllerFor(h.oidc.request['auth_req_id'] as String);
      final pending = c.load();
      c.dispose();
      await pending;
    });

    test('deny after the code already failed ends the request', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      final c = await h.open();
      h.oidc.status = 'code_failed'; // ended on another screen meanwhile
      await c.deny();
      expect(c.phase, ApprovalPhase.codeFailed);
    });

    test('a decided request reopens as unavailable, not ready', () async {
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      await (await h.open()).approve('483920');
      final again = await h.open();
      expect(again.phase, ApprovalPhase.unavailable);
      expect(again.error, contains('already decided'));
      expect(again.request, isNull);
    });

    test('deny is signed without a code or biometric check', () async {
      final h = Harness(vectorRequest('direct-transaction-deny'));
      final c = await h.open();
      await c.deny();
      expect(c.phase, ApprovalPhase.denied, reason: c.error);
      expect(h.gate.asked, 0);
      final s = h.oidc.receipts.single['signed'] as Map;
      expect(s['decision'], 'deny');
      expect(s['binding_code'], '');
      expect(s['binding_message'], 'Invoice 2026-0043');
      expect((await h.history.read()).single.decision, 'deny');
    });
  });

  group('screens', () {
    void tall(WidgetTester tester) {
      tester.view.physicalSize = const Size(800, 3000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    Widget card(Map<String, Object?> json) => MaterialApp(
        home: Scaffold(
            body: SingleChildScrollView(
                child: ApprovalCard(request: ConsentRequest.fromJson(json)))));

    String textOf(WidgetTester tester, String key) {
      final w = tester.widget(find.descendant(
          of: find.byKey(Key(key)), matching: find.byType(SelectableText)));
      return (w as SelectableText).data!;
    }

    testWidgets('transaction card fields match authorization_details',
        (tester) async {
      tall(tester);
      final json = vectorRequest('direct-transaction-approve-verified');
      final d = ((json['authorization_details'] as List).single as Map);
      await tester.pumpWidget(card(json));
      expect(find.byKey(const Key('client-verified')), findsOneWidget);
      expect(find.text('Example Bank'), findsOneWidget);
      expect(find.text('bank.example'), findsOneWidget);
      final asset = d['asset'] as Map;
      final fiat = d['fiat_value'] as Map;
      expect(find.text('${d['amount']} ${asset['symbol']}'), findsOneWidget);
      expect(
          find.text('${fiat['amount']} ${fiat['currency']}'), findsOneWidget);
      expect(textOf(tester, 'fact-network'), d['network']);
      expect(textOf(tester, 'fact-sender'), d['sender']);
      expect(textOf(tester, 'fact-receiver'), d['receiver']);
      expect(textOf(tester, 'fact-hash'), d['group_hash']);
      expect(textOf(tester, 'fact-asset'),
          '${asset['symbol']} (id ${asset['id']}, ${asset['decimals']} decimals)');
      expect(textOf(tester, 'fact-origin'), json['origin']);
      expect(find.byKey(const Key('binding-message')), findsOneWidget);
      expect(find.text('Invoice 2026-0042'), findsOneWidget);
    });

    testWidgets('limits change card fields match authorization_details',
        (tester) async {
      tall(tester);
      final json = vectorRequest('stepup-limits-change-approve');
      final d = ((json['authorization_details'] as List).single as Map);
      final cur = d['current_limits'] as Map;
      final req = d['requested_limits'] as Map;
      await tester.pumpWidget(card(json));
      expect(textOf(tester, 'fact-per-transaction'),
          '${cur['per_transaction_eur']} EUR → ${req['per_transaction_eur']} EUR');
      expect(textOf(tester, 'fact-per-day'),
          '${cur['per_day_eur']} EUR → ${req['per_day_eur']} EUR');
      expect(textOf(tester, 'fact-hash'), d['request_hash']);
    });

    testWidgets('unverified client: warning, fingerprint, no message',
        (tester) async {
      tall(tester);
      final json = vectorRequest('direct-transaction-approve-unverified');
      await tester.pumpWidget(card({...json, 'binding_message': 'trust me'}));
      expect(find.byKey(const Key('client-unverified')), findsOneWidget);
      expect(
          find.text(
              'Unverified application ${(json['client'] as Map)['fingerprint']}'),
          findsOneWidget);
      expect(find.byKey(const Key('binding-message')), findsNothing);
      expect(find.text('trust me'), findsNothing);
    });

    testWidgets('approve from the screen and see it in history',
        (tester) async {
      tall(tester);
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      await tester.pumpWidget(MaterialApp(
          home: ApprovalScreen(
              services: h.services,
              authReqId: h.oidc.request['auth_req_id'] as String)));
      await tester.pump();
      await tester.pump();
      expect(find.byType(ApprovalCard), findsOneWidget);
      await tester.enterText(find.byKey(const Key('binding-code')), '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Approved.'), findsOneWidget);

      await tester
          .pumpWidget(MaterialApp(home: HistoryScreen(history: h.history)));
      await tester.pump();
      expect(find.textContaining('Approved: 5000.000000 USDC'), findsOneWidget);
    });

    testWidgets('offline approve shows the offline error', (tester) async {
      tall(tester);
      final h = Harness(vectorRequest('stepup-transaction-approve'));
      await tester.pumpWidget(MaterialApp(
          home: ApprovalScreen(
              services: h.services,
              authReqId: h.oidc.request['auth_req_id'] as String)));
      await tester.pump();
      await tester.pump();
      h.oidc.offline = true;
      await tester.enterText(find.byKey(const Key('binding-code')), '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.textContaining('Offline. Nothing was approved.'),
          findsOneWidget);
      expect(find.byKey(const Key('approve')), findsOneWidget);
    });
  });
}
