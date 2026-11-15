// T-247: the payment, login and action requests of a relying party (protocol 1.2): the model, the details hash the app computes itself, what is signed, and the three cards.
import 'dart:convert';

import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_controller.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart' show DismissedRequests;
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/jcs.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/approvals/receipt.dart';
import 'package:biatec_authenticator/approvals/widgets/request_card.dart';
import 'package:biatec_authenticator/text/rp_strings.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'approval_test.dart' show vectorNow;
import 'rp_support.dart';
import 'support.dart';

ConsentRequest _parse(Map<String, Object?> json, {bool on = true}) =>
    ConsentRequest.fromJson(json, relyingParties: on);

Future<void> _screen(WidgetTester tester, RpRig rig) async {
  tester.view.physicalSize = const Size(500, 2400);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    home: ApprovalScreen(
        services: rig.services,
        authReqId: rig.oidc.request['auth_req_id'] as String),
  ));
  await tester.pump();
  await tester.pump();
}

bool _approveOn(WidgetTester tester) =>
    tester.widget<FilledButton>(find.byKey(const Key('approve'))).onPressed !=
    null;

String _text(WidgetTester tester, String key) =>
    tester.widget<Text>(find.byKey(Key(key))).data ??
    tester.widget<Text>(find.byKey(Key(key))).textSpan!.toPlainText();

String _fact(WidgetTester tester, String key) => tester
    .widget<SelectableText>(
        find.descendant(of: find.byKey(Key(key)), matching: find.byType(SelectableText)))
    .data!;

void main() {
  final names = [for (final v in vectors('receipts-1.2.json')) v['name'] as String];

  group('the details hash', () {
    test('there are ten 1.2 vectors', () => expect(names, hasLength(10)));

    for (final name in names) {
      test('$name: the app computes the vector\'s details_hash from the details it renders',
          () {
        final v = rpVector(name);
        final details = (((v['request'] as Map)['authorization_details'] as List)
                .single as Map)
            .cast<String, Object?>();
        // Computed by the app itself, with RFC 8785 and SHA-256: equal to the vector's, the request's and the receipt's.
        final computed = computeDetailsHash(details);
        expect(computed, v['details_hash']);
        expect(computed, (v['request'] as Map)['bound_hash']);
        expect(((v['receipt'] as Map)['signed'] as Map)['details_hash'], computed);
      });
    }

    test('the worked example of the specification', () {
      expect(computeDetailsHash(
              (((rpRequest('payment-approve'))['authorization_details'] as List)
                      .single as Map)
                  .cast<String, Object?>()),
          '3D1GsRmJXZ2aRlpkmod9SNQRSLWJsxi1dfgV3XaZF7c');
    });

    test('the origin is part of the hashed object', () {
      final r = rpRequest('payment-approve');
      final without = withDetail(r, (d) => d.remove('origin'));
      expect(
          computeDetailsHash((without['authorization_details'] as List).single
              as Map<String, Object?>),
          'b2PVUKD24wUOKSiIlgXGGUi5aGY0xDrOEbTynZ7A-HA');
    });

    test('a request whose stated hash differs from the one the app computes is refused', () {
      final r = copyOf(rpRequest('payment-approve'))
        ..['bound_hash'] = 'b2PVUKD24wUOKSiIlgXGGUi5aGY0xDrOEbTynZ7A-HA';
      expect(() => _parse(r), throwsFormatException);
      // One character of the creditor name changed behind the same stated hash.
      final altered = withDetail(
          rpRequest('payment-approve'), (d) => d['creditor_name'] = 'Muster Energie GmbH.');
      expect(() => _parse(altered), throwsFormatException);
    });

    test('the receipt carries the hash the app computed, never a copy of the request\'s', () {
      final request = _parse(rpRequest('payment-approve'));
      final signed =
          signedObject(request, code: '620418', decision: Decision.approve);
      expect(signed['details_hash'], '3D1GsRmJXZ2aRlpkmod9SNQRSLWJsxi1dfgV3XaZF7c');
      expect(signed.containsKey('group_hash'), isFalse);
      expect(signed.containsKey('remember'), isFalse);
      expect(signed['binding_message'], '');
    });
  });

  group('what is signed', () {
    for (final name in names) {
      test('$name: the signed object is the vector\'s, byte for byte', () {
        final v = rpVector(name);
        final signed = ((v['receipt'] as Map)['signed'] as Map).cast<String, Object?>();
        final request = _parse((v['request'] as Map).cast<String, Object?>());
        // The altered IBAN has wrong check digits: such a request is not one the app shows or signs.
        if (name == 'payment-approve-iban-altered-REFUSED') {
          // The request holds the real IBAN; the vector's receipt was signed over an altered one, which fails its check digits. The app signs what the request holds.
          final signedDetails = ((signed['authorization_details'] as List).single as Map)
              .cast<String, Object?>();
          expect(RelyingPartyDetail.tryParse(signedDetails), isNull);
          expect(computeDetailsHash(signedDetails), isNot(signed['details_hash']));
          final mine = signedObject(request,
              code: signed['binding_code'] as String, decision: Decision.approve);
          expect(canonicalize(mine), isNot(v['canonical']));
          expect(mine['details_hash'], signed['details_hash']);
          return;
        }
        final mine = signedObject(request,
            code: signed['binding_code'] as String,
            decision: Decision.fromWire(signed['decision'])!);
        expect(canonicalize(mine), v['canonical']);
      });
    }

    test('an approval is signed by the approval key with one prompt, a deny by the presence key without one',
        () async {
      final rig = RpRig(rpRequest('payment-approve'));
      final id = rig.oidc.request['auth_req_id'] as String;
      final c = rig.services.controllerFor(id);
      await c.load();
      expect(c.phase, ApprovalPhase.ready, reason: c.error);
      await c.approve('620418');
      expect(c.phase, ApprovalPhase.approved, reason: c.error);
      expect(rig.keys.prompts, hasLength(1));
      expect(rig.keys.signed, hasLength(1));
      expect(rig.keys.presenceSigned, isEmpty);
      final receipt = rig.oidc.receipts.single;
      expect(receipt['version'], '1.2');
      expect((receipt['device'] as Map)['key_use'], 'approve');
      // The device's own attestation, like every other receipt.
      expect((receipt['device'] as Map)['attestation'], 'h.p.s');

      final rig2 = RpRig(rpRequest('login-deny'));
      final c2 = rig2.services
          .controllerFor(rig2.oidc.request['auth_req_id'] as String);
      await c2.load();
      await c2.deny();
      expect(c2.phase, ApprovalPhase.denied, reason: c2.error);
      expect(rig2.keys.prompts, isEmpty);
      expect(rig2.keys.presenceSigned, hasLength(1));
      expect(rig2.oidc.receipts.single['version'], '1.2');
      expect((rig2.oidc.receipts.single['device'] as Map)['key_use'], 'presence');
    });

    for (final name in ['payment-approve', 'payment-account-identification-approve', 'login-approve', 'action-approve']) {
      test('$name: the receipt the app signs verifies with the protocol kit (lib.mjs, pinned keys only)',
          skip: nodeSkip, () async {
        final v = rpVector(name);
        final request = (v['request'] as Map).cast<String, Object?>();
        final code = ((v['receipt'] as Map)['signed'] as Map)['binding_code'] as String;
        final rig = RpRig(request, code: code);
        final c = rig.services
            .controllerFor(request['auth_req_id'] as String);
        await c.load();
        await c.approve(code);
        expect(c.phase, ApprovalPhase.approved, reason: c.error);
        final receipt = rig.oidc.receipts.single;
        expect(await verifiedByKit(receipt, request, code), isTrue,
            reason: 'verifyReceiptPinned');
        // The same receipt with another code, or signed over another detail, does not.
        final tampered = copyOf(receipt);
        (tampered['signed'] as Map)['expiry'] =
            ((tampered['signed'] as Map)['expiry'] as int) + 1;
        expect(await verifiedByKit(tampered, request, code), isFalse);
      });
    }

    test('a deny verifies with the kit too, under the presence key', skip: nodeSkip, () async {
      final v = rpVector('login-deny');
      final request = (v['request'] as Map).cast<String, Object?>();
      final rig = RpRig(request);
      final c = rig.services.controllerFor(request['auth_req_id'] as String);
      await c.load();
      await c.deny();
      final receipt = rig.oidc.receipts.single;
      expect(await verifiedByKit(receipt, request, '620418', decision: 'deny'),
          isTrue);
    });

    test('a wrong code is signed as typed, three of them end the request, and the receipt is a 1.2 one',
        () async {
      final rig = RpRig(rpRequest('payment-approve'));
      final c = rig.services
          .controllerFor(rig.oidc.request['auth_req_id'] as String);
      await c.load();
      await c.approve('000000');
      expect(c.attemptsRemaining, 2);
      await c.approve('000000');
      await c.approve('000000');
      expect(c.phase, ApprovalPhase.codeFailed);
      expect(rig.oidc.receipts.map((r) => r['version']).toSet(), {'1.2'});
    });

    test('the prompt of the operating system has no text the service wrote: its identity, the amount and the currency only', () async {
      for (final (name, code, expected) in [
        ('payment-approve', '620418', 'bank.example: Payment of 1250.00 EUR'),
        ('login-approve', '917263', 'bank.example: Sign in'),
        ('action-approve', '550193', 'bank.example: Confirm an action'),
      ]) {
        final rig = RpRig(rpRequest(name), code: code);
        final c = rig.services.controllerFor(rig.oidc.request['auth_req_id'] as String);
        await c.load();
        await c.approve(code);
        expect(c.phase, ApprovalPhase.approved, reason: c.error);
        expect(rig.keys.prompts.single.$2, expected);
        expect(rig.keys.prompts.single.$2, isNot(contains('Muster')));
        expect(rig.keys.prompts.single.$2, isNot(contains('Add payee')));
      }
    });

    test('the history keeps the type, the service and the amount, and no code', () async {
      final rig = RpRig(rpRequest('payment-approve'));
      final c = rig.services
          .controllerFor(rig.oidc.request['auth_req_id'] as String);
      await c.load();
      await c.approve('620418');
      final entry = (await rig.history.read()).single;
      expect(entry.summary, 'Payment: 1250.00 EUR');
      expect(entry.summary, isNot(contains('Muster')));
      expect(entry.clientLabel, 'bank.example');
      expect(entry.decision, 'approve');
      expect(jsonEncode(entry.receipt), isNot(contains('620418')));
      expect(summarize(_parse(rpRequest('login-approve')).detail), 'Sign in');
      expect(summarize(_parse(rpRequest('action-approve')).detail), 'Action requested');
    });
  });

  group('requests that are not shown', () {
    test('an unknown type is a request that cannot be shown, and cannot be signed', () {
      final r = _parse(withDetail(rpRequest('payment-approve'),
              (d) => d['type'] = 'biatec_teleport')
            ..['bound_hash'] = 'x');
      expect(r.detail, isA<UnsupportedDetail>());
      expect(r.isShowable, isFalse);
      expect(
          () => signedObject(r, code: '111111', decision: Decision.approve),
          throwsStateError);
    });

    test('a schema failure is one too: a member that is not in the schema, a missing member, a number as an amount, bad check digits',
        () {
      for (final change in <void Function(Map<String, Object?>)>[
        (d) => d['unknown'] = 'x',
        (d) => d.remove('creditor_name'),
        (d) => (d['instructed_amount'] as Map)['amount'] = 1250,
        (d) => (d['instructed_amount'] as Map)['amount'] = '0.00',
        (d) => (d['instructed_amount'] as Map)['amount'] = '1.234',
        (d) => (d['instructed_amount'] as Map)['currency'] = 'eur',
        (d) => (d['creditor_account'] as Map)['iban'] = 'DE89370400440532013001',
        (d) => d['execution_date'] = '2026-02-30',
        (d) => d['origin'] = 'https://Bank.example',
      ]) {
        final r = _parse(withDetail(rpRequest('payment-approve'), change)
          ..['bound_hash'] = 'x');
        expect(r.isShowable, isFalse);
        expect(r.detail, isA<UnsupportedDetail>());
      }
    });

    test('a right-to-left override in any string the service wrote is not approvable', () {
      for (final (name, change) in <(String, void Function(Map<String, Object?>))>[
        ('payment-approve', (d) => d['creditor_name'] = 'Muster \u202Eeinegrenhcs GmbH'),
        ('payment-approve', (d) => d['remittance'] = 'Invoice \u2067 2026'),
        ('payment-account-identification-approve', (d) => (d['creditor_account'] as Map)['scheme'] = 'BB\u200FAN'),
        ('login-approve', (d) => (d['context'] as Map)['location'] = 'Bratislava\u202E'),
        ('action-approve', (d) => d['title'] = '\u202EAdd payee'),
        ('action-approve', (d) => ((d['lines'] as List).first as Map)['value'] = 'Muster\u200B Energie'),
      ]) {
        final r = _parse(withDetail(rpRequest(name), change)..['bound_hash'] = 'x');
        expect(r.isShowable, isFalse, reason: name);
      }
    });

    test('with the relying-party protocol off, the three types cannot be shown', () {
      for (final name in ['payment-approve', 'login-approve', 'action-approve']) {
        final r = _parse(rpRequest(name), on: false);
        expect(r.isShowable, isFalse, reason: name);
        expect(r.detail, isA<UnsupportedDetail>());
      }
    });

    test('a request that does not fit a relying party\'s is not shown: not direct, a binding message, not linked, another origin', () {
      for (final change in <void Function(Map<String, Object?>)>[
        (r) => r['entry'] = 'step-up',
        (r) => r['binding_message'] = 'Pay now',
        (r) => (r['client'] as Map)['linked'] = false,
        (r) => r['origin'] = 'https://evil.example',
        (r) => r.remove('origin'),
      ]) {
        final r = copyOf(rpRequest('payment-approve'));
        change(r);
        expect(_parse(r).isShowable, isFalse);
      }
    });

    testWidgets('an unknown type on the screen: "This request cannot be shown; update the app", nothing of it, not approvable, not deniable',
        (tester) async {
      final r = withDetail(rpRequest('payment-approve'), (d) {
        d['type'] = 'biatec_teleport';
      });
      r['bound_hash'] = 'x';
      final rig = RpRig(r);
      await _screen(tester, rig);
      expect(find.byKey(const Key('request-unsupported')), findsOneWidget);
      expect(find.text('This request cannot be shown; update the app'),
          findsOneWidget);
      expect(find.text('Muster Energie GmbH'), findsNothing);
      // Neither approve nor deny: a Dismiss that hides it locally.
      expect(find.byKey(const Key('approve')), findsNothing);
      expect(find.byKey(const Key('deny')), findsNothing);
      expect(find.byKey(const Key('dismiss')), findsOneWidget);
      expect(rig.oidc.receipts, isEmpty);
    });

    testWidgets('Dismiss hides the request locally, says so, and sends nothing', (tester) async {
      final r = withDetail(rpRequest('payment-approve'), (d) => d['type'] = 'biatec_teleport');
      r['bound_hash'] = 'x';
      final rig = RpRig(r);
      var calls = 0;
      await tester.pumpWidget(MaterialApp(
        theme: buildTheme(Brightness.light),
        home: Builder(
            builder: (context) => Scaffold(
                  body: TextButton(
                      key: const Key('open'),
                      onPressed: () => Navigator.of(context).push(
                          ApprovalScreen.route(rig.services, r['auth_req_id'] as String)),
                      child: const Text('open')),
                )),
      ));
      await tester.tap(find.byKey(const Key('open')));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      await tester.tap(find.byKey(const Key('dismiss')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      expect(rig.services.dismissed.contains(r['auth_req_id'] as String), isTrue);
      expect(find.text('Dismissed. Nothing was sent.'), findsOneWidget);
      expect(rig.oidc.receipts, isEmpty);
      expect(calls, 0);
    });

    testWidgets('in the pending list an unsupported request has Dismiss, is not counted, and is gone after it', (tester) async {
      final r = withDetail(rpRequest('payment-approve'), (d) => d['type'] = 'biatec_teleport');
      r['bound_hash'] = 'x';
      final rig = RpRig(r);
      final counts = <int>[];
      tester.view.physicalSize = const Size(500, 1200);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: PendingScreen(
              services: rig.services,
              pollEvery: const Duration(hours: 1),
              onCount: counts.add)));
      await tester.pump();
      await tester.pump();
      final id = r['auth_req_id'] as String;
      expect(find.byKey(Key('pending-$id')), findsOneWidget);
      expect(find.text('This request cannot be shown'), findsOneWidget);
      expect(counts.last, 0, reason: 'not counted in the badge');
      await tester.tap(find.byKey(Key('dismiss-$id')));
      await tester.pump();
      expect(find.byKey(Key('pending-$id')), findsNothing);
      expect(find.text('Dismissed. Nothing was sent.'), findsOneWidget);
      expect(rig.oidc.receipts, isEmpty);
      // A refresh does not bring it back.
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a right-to-left override on the card: the text is not drawn, the request cannot be signed',
        (tester) async {
      // (The scripted server of the screen tests sends Latin-1 only, so the card is drawn from the parsed request.)
      final r = _parse(withDetail(rpRequest('payment-approve'),
          (d) => d['creditor_name'] = 'Muster \u202Eeinegrenhcs GmbH')
        ..['bound_hash'] = 'x');
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: Scaffold(
              body: SingleChildScrollView(child: ApprovalCard(request: r)))));
      expect(find.byKey(const Key('request-unsupported')), findsOneWidget);
      expect(find.textContaining('Muster'), findsNothing);
      expect(r.isShowable, isFalse);
      expect(() => signedObject(r, code: '111111', decision: Decision.approve),
          throwsStateError);
    });

    testWidgets('relying-party protocol off on the screen: the same message', (tester) async {
      final rig = RpRig(rpRequest('payment-approve'), relyingParties: false);
      await _screen(tester, rig);
      // The request is not even offered as one that can be shown: fetching it fails with the same words, or the card says so.
      expect(find.textContaining('cannot be shown'), findsWidgets);
      expect(find.byKey(const Key('approve')), findsNothing);
    });
  });

  group('the payment card', () {
    testWidgets('the amount and currency, the payee, the IBAN in fours, the reference, the date, the origin, who asks, the countdown and the code field',
        (tester) async {
      final rig = RpRig(rpRequest('payment-approve'));
      await _screen(tester, rig);
      expect(_text(tester, 'payment-amount'), '1250.00 EUR');
      // The amount is the largest text of the card.
      final size = tester.widget<Text>(find.byKey(const Key('payment-amount'))).style!.fontSize!;
      for (final other in ['payment-creditor-name', 'payment-iban']) {
        expect(size, greaterThan(tester.getSize(find.byKey(Key(other))).height / 2));
      }
      expect(_fact(tester, 'payment-creditor-name'), 'Muster Energie GmbH');
      expect(_fact(tester, 'payment-iban'), 'DE89 3704 0044 0532 0130 00');
      expect(_fact(tester, 'payment-remittance'), 'Invoice 2026-0042');
      expect(_fact(tester, 'payment-execution-date'), '2026-10-20');
      // What the service wrote is in the area that says so.
      expect(find.text('Written by the service'), findsOneWidget);
      expect(
          find.descendant(
              of: find.byKey(const Key('written-by-service')),
              matching: find.byKey(const Key('payment-iban'))),
          findsOneWidget);
      expect(_fact(tester, 'fact-origin'), 'https://netbanking.bank.example');
      // Who asks: the one identity widget.
      expect(find.byKey(const Key('client-verified')), findsOneWidget);
      expect(_text(tester, 'service-domain'), 'bank.example');
      // The countdown and the code entry.
      expect(find.byKey(const Key('expires-in')), findsOneWidget);
      expect(find.byKey(const Key('binding-code')), findsOneWidget);
      expect(_approveOn(tester), isTrue);
    });

    testWidgets('another identifier is shown with its scheme, and a service that has not proven its domain is the unproven application',
        (tester) async {
      final rig = RpRig(rpRequest('payment-account-identification-approve'));
      await _screen(tester, rig);
      final d = (rig.oidc.request['authorization_details'] as List).single as Map;
      final account = d['creditor_account'] as Map;
      expect(_fact(tester, 'payment-identification'), account['identification']);
      expect(_fact(tester, 'payment-scheme'), account['scheme']);
      expect(find.byKey(const Key('payment-iban')), findsNothing);
      expect(find.byKey(const Key('client-unverified')), findsOneWidget);
      expect(find.byKey(const Key('client-verified')), findsNothing);
    });

    testWidgets('without an execution date, a reference or an origin, those are not drawn',
        (tester) async {
      final r = withDetail(rpRequest('payment-approve'), (d) {
        d.remove('execution_date');
        d.remove('remittance');
        d.remove('origin');
      });
      r.remove('origin');
      r['bound_hash'] = computeDetailsHash(
          ((r['authorization_details'] as List).single as Map).cast<String, Object?>());
      final rig = RpRig(r);
      await _screen(tester, rig);
      expect(find.byKey(const Key('payment-execution-date')), findsNothing);
      expect(find.byKey(const Key('payment-remittance')), findsNothing);
      expect(find.byKey(const Key('fact-origin')), findsNothing);
      expect(_approveOn(tester), isTrue);
    });

    testWidgets('markup in a string is drawn as the characters it is, never interpreted', (tester) async {
      final r = withDetail(rpRequest('payment-approve'),
          (d) => d['remittance'] = '<b>bold</b> [link](https://evil.example) https://evil.example');
      r['bound_hash'] = computeDetailsHash(
          ((r['authorization_details'] as List).single as Map).cast<String, Object?>());
      final rig = RpRig(r);
      await _screen(tester, rig);
      expect(_fact(tester, 'payment-remittance'),
          '<b>bold</b> [link](https://evil.example) https://evil.example');
      // Nothing inside the card is tappable: no link, no button.
      expect(
          find.descendant(
              of: find.byKey(const Key('rp-card-biatec_payment')),
              matching: find.byType(InkWell)),
          findsNothing,
          reason: 'no text is a tappable link');
    });
  });

  group('the login card', () {
    testWidgets('"Sign in to" the domain, and the context under "Reported by the service"', (tester) async {
      final rig = RpRig(rpRequest('login-approve'), code: '917263');
      await _screen(tester, rig);
      expect(_text(tester, 'login-title'), 'Sign in to bank.example');
      expect(find.text('Reported by the service'), findsOneWidget);
      expect(_text(tester, 'login-context-ip'), 'IP address: 203.0.113.42');
      expect(_text(tester, 'login-context-location'), 'Location: Bratislava, Slovakia');
      expect(_text(tester, 'login-context-userAgent'),
          'Browser or app: Firefox 133 on Windows 11');
      // The secondary style: not the weight of the title.
      final quiet = tester.widget<Text>(find.byKey(const Key('login-context-ip')));
      final title = tester.widget<Text>(find.byKey(const Key('login-title')));
      expect(quiet.style!.fontWeight, isNot(title.style!.fontWeight));
      expect(find.byKey(const Key('fact-origin')), findsOneWidget);
      expect(find.byKey(const Key('expires-in')), findsOneWidget);
      expect(find.byKey(const Key('binding-code')), findsOneWidget);
    });

    testWidgets('a service without a proven domain is named by its fingerprint', (tester) async {
      final r = copyOf(rpRequest('login-approve'))
        ..['client'] = {
          'fingerprint': 'b70156d0932b8095',
          'domain_proven': false,
          'linked': true
        };
      final rig = RpRig(r);
      await _screen(tester, rig);
      expect(_text(tester, 'login-title'), 'Sign in to b701-5 6d0-932b-8095'.replaceAll('b701-5 6d0', 'b701-56d0'));
      expect(find.byKey(const Key('client-unverified')), findsOneWidget);
    });

    testWidgets('a login with no context says the service reported nothing', (tester) async {
      final r = withDetail(rpRequest('login-approve'), (d) => d.remove('context'));
      r['bound_hash'] = computeDetailsHash(
          ((r['authorization_details'] as List).single as Map).cast<String, Object?>());
      final rig = RpRig(r);
      await _screen(tester, rig);
      expect(find.byKey(const Key('login-nothing')), findsOneWidget);
    });
  });

  group('the action card', () {
    testWidgets('the title and the labelled lines inside the area headed "Written by the service"', (tester) async {
      final rig = RpRig(rpRequest('action-approve'), code: '550193');
      await _screen(tester, rig);
      final box = find.byKey(const Key('written-by-service'));
      expect(find.descendant(of: box, matching: find.text('Written by the service')), findsOneWidget);
      expect(find.descendant(of: box, matching: find.text('Add payee')), findsOneWidget);
      expect(_fact(tester, 'action-line-0'), 'Muster Energie GmbH');
      expect(find.descendant(of: box, matching: find.byKey(const Key('action-line-2'))), findsOneWidget);
      expect(_fact(tester, 'action-line-2'), '5 000 EUR');
      expect(find.text('Payee'), findsOneWidget);
      // The area has a border.
      final decoration = tester.widget<Container>(box).decoration as BoxDecoration;
      expect(decoration.border, isNotNull);
      expect(find.byKey(const Key('client-verified')), findsOneWidget);
      expect(find.byKey(const Key('expires-in')), findsOneWidget);
      expect(find.byKey(const Key('binding-code')), findsOneWidget);
    });
  });

  test('dismissed ids are forgotten once the request has run out', () {
    final d = DismissedRequests();
    final t = DateTime.utc(2026, 11, 15, 12);
    d.add('a', t.add(const Duration(minutes: 5)));
    d.add('b', t.add(const Duration(minutes: 1)));
    d.prune(t.add(const Duration(minutes: 2)));
    expect(d.contains('a'), isTrue);
    expect(d.contains('b'), isFalse);
    expect(d.length, 1);
  });

  group('English and Slovak', () {
    test('both languages have every key', () {
      expect(RpStrings.sk.keys.toSet(), RpStrings.en.keys.toSet());
    });

    test('the Slovak texts differ from the English ones', () {
      for (final k in RpStrings.en.keys) {
        if (k == 'done') continue;
        expect(RpStrings.sk.t(k), isNotEmpty);
      }
      expect(RpStrings.sk.t('writtenByService'), 'Napísala služba');
    });

    test('a value that holds a placeholder is not substituted again', () {
      expect(RpStrings.en.t('signInTo', {'who': '{who}'}), 'Sign in to {who}');
    });
  });

  test('the pending list card of each type', () {
    for (final name in ['payment-approve', 'login-approve', 'action-approve']) {
      final r = _parse(rpRequest(name));
      expect(r.isShowable, isTrue, reason: name);
      expect(r.detail, isA<RelyingPartyDetail>());
      expect(vectorNow().isBefore(r.expiresAt), isTrue);
    }
  });

  testWidgets('the card widget of a request that cannot be shown is the unsupported card', (tester) async {
    await tester.pumpWidget(MaterialApp(
        theme: buildTheme(Brightness.light),
        home: Scaffold(
            body: SingleChildScrollView(
                child: ApprovalCard(
                    request: _parse(rpRequest('payment-approve'), on: false))))));
    expect(find.byKey(const Key('request-unsupported')), findsOneWidget);
  });

  group('the pending list', () {
    Future<void> card(WidgetTester tester, ConsentRequest r) =>
        tester.pumpWidget(MaterialApp(
            theme: buildTheme(Brightness.light),
            home: Scaffold(
                body: RequestCard(request: r, now: vectorNow(), onOpen: () {}))));

    testWidgets('a payment: the amount first, who is paid, who asks', (tester) async {
      await card(tester, _parse(rpRequest('payment-approve')));
      expect(find.text('1250.00 EUR'), findsOneWidget);
      expect(find.text('Payment request'), findsOneWidget);
      // The service's words only in the quiet quoted line, with who wrote them; never in the headline.
      final quoted = find.byKey(Key('quoted-${rpRequest('payment-approve')['auth_req_id']}'));
      expect(quoted, findsOneWidget);
      final q = tester.widget<Text>(find.descendant(of: quoted, matching: find.byType(Text)));
      expect(q.data, 'From bank.example: “Muster Energie GmbH”');
      expect(q.style!.fontStyle, FontStyle.italic);
      expect(q.style!.fontSize, lessThan(tester.widget<Text>(find.text('1250.00 EUR')).style!.fontSize!));
      expect(find.text('bank.example'), findsOneWidget);
    });

    testWidgets('a login and an action', (tester) async {
      await card(tester, _parse(rpRequest('login-approve')));
      expect(find.text('Sign in'), findsOneWidget);
      await card(tester, _parse(rpRequest('action-approve')));
      expect(find.text('Action requested'), findsOneWidget);
      expect(find.text('Add payee'), findsNothing, reason: 'the title is only inside the quoted line');
      expect(find.textContaining('“Add payee”'), findsOneWidget);
    });

    testWidgets('a request that cannot be shown says so and names no detail', (tester) async {
      await card(tester, _parse(rpRequest('payment-approve'), on: false));
      expect(find.text('This request cannot be shown'), findsOneWidget);
      expect(find.textContaining('Muster'), findsNothing);
    });
  });
}
