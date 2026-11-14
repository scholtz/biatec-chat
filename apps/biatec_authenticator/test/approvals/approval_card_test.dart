import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'approval_test.dart' show Harness, vectorRequest, vectorRequest10;

Future<void> _pumpCard(WidgetTester tester, Map<String, Object?> json) async {
  tester.view.physicalSize = const Size(500, 4000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    home: Scaffold(
      body: SingleChildScrollView(
          child: ApprovalCard(request: ConsentRequest.fromJson(json))),
    ),
  ));
}

String _selectable(WidgetTester tester, Key key) => tester
    .widget<SelectableText>(find.descendant(
        of: find.byKey(key), matching: find.byType(SelectableText)))
    .data!;

void main() {
  group('protocol 1.1 card', () {
    testWidgets('a two-transfer group shows every transfer and the total',
        (tester) async {
      final json = vectorRequest('stepup-two-transfers-approve');
      final r = ConsentRequest.fromJson(json);
      final g = (r.detail as TransactionDetail).group!;
      expect(g.transfers, hasLength(2));
      await _pumpCard(tester, json);
      for (var i = 0; i < 2; i++) {
        final t = g.transfers[i];
        expect(find.byKey(Key('transfer-$i')), findsOneWidget);
        expect(tester.widget<Text>(find.byKey(Key('transfer-$i-amount'))).data,
            '${t.amount} ${t.asset.symbol}');
        expect(tester.widget<Text>(find.byKey(Key('transfer-$i-fiat'))).data,
            '${t.fiat}');
        expect(_selectable(tester, Key('transfer-$i-receiver')), t.receiver);
        expect(find.textContaining('id ${t.asset.id}'), findsWidgets);
      }
      expect(find.byKey(const Key('transfer-2')), findsNothing);
      // The group total is the hero, the origin is labelled as reported.
      expect(find.text('${g.total}'), findsWidgets);
      expect(find.byKey(const Key('value-unknown')), findsNothing);
      expect(_selectable(tester, const Key('fact-origin')), r.origin);
      expect(
          find.text('Origin, as reported by the application'), findsOneWidget);
    });

    testWidgets('an untrusted application is shown in the warning style',
        (tester) async {
      final json =
          vectorRequest('stepup-untrusted-application-approve-remember');
      await _pumpCard(tester, json);
      final app = find.byKey(const Key('application-0'));
      expect(app, findsOneWidget);
      expect(
          find.descendant(
              of: app, matching: find.text('Calls application 7654321')),
          findsOneWidget);
      expect(
          find.descendant(
              of: app, matching: find.byIcon(Icons.warning_amber_rounded)),
          findsOneWidget);
    });

    testWidgets('a trusted application is listed without a warning',
        (tester) async {
      final json =
          vectorRequest('stepup-transfer-plus-trusted-application-approve');
      await _pumpCard(tester, json);
      final app = find.byKey(const Key('application-0'));
      expect(app, findsOneWidget);
      expect(_selectable(tester, const Key('application-0')), '1234567');
      expect(
          find.descendant(
              of: app, matching: find.byIcon(Icons.warning_amber_rounded)),
          findsNothing);
    });

    testWidgets('an unknown value says "Value unknown" in the warning style',
        (tester) async {
      final json = vectorRequest('stepup-value-unknown-approve');
      await _pumpCard(tester, json);
      final banner = find.byKey(const Key('value-unknown'));
      expect(banner, findsOneWidget);
      expect(find.descendant(of: banner, matching: find.text('Value unknown')),
          findsOneWidget);
      expect(
          find.descendant(
              of: banner, matching: find.byIcon(Icons.warning_amber_rounded)),
          findsOneWidget);
      // The transfer that could not be priced says so too; no invented total.
      expect(tester.widget<Text>(find.byKey(const Key('transfer-0-fiat'))).data,
          'Value unknown');
    });

    testWidgets('a proven domain is the client; the fingerprint is not a name',
        (tester) async {
      final json = vectorRequest('direct-transaction-approve-domain-proven');
      final c = ConsentRequest.fromJson(json).client;
      await _pumpCard(tester, json);
      expect(find.byKey(const Key('client-verified')), findsOneWidget);
      expect(find.text('bank.example'), findsOneWidget);
      expect(find.textContaining(c.fingerprintShown!), findsOneWidget);
      expect(find.byKey(const Key('client-unverified')), findsNothing);
    });

    testWidgets('an unproven application shows only the fingerprint',
        (tester) async {
      final json = vectorRequest('direct-transaction-approve-domain-unproven');
      final c = ConsentRequest.fromJson(json).client;
      await _pumpCard(tester, {...json, 'binding_message': 'I am Your Bank'});
      final banner = find.byKey(const Key('client-unverified'));
      expect(banner, findsOneWidget);
      expect(
          find.descendant(
              of: banner,
              matching:
                  find.text('Unproven application ${c.fingerprintShown}')),
          findsOneWidget);
      expect(find.text('I am Your Bank'), findsNothing);
      expect(find.byKey(const Key('client-verified')), findsNothing);
    });
  });

  group('a protocol 1.0 request', () {
    const name = 'stepup-transaction-approve';

    testWidgets('is refused on the card, nothing of it is shown',
        (tester) async {
      await _pumpCard(tester, vectorRequest10(name));
      expect(find.byKey(const Key('request-unsupported')), findsOneWidget);
      expect(find.text('This request cannot be shown; update the app'),
          findsOneWidget);
      expect(find.byKey(const Key('fact-amount')), findsNothing);
      expect(find.byKey(const Key('fact-receiver')), findsNothing);
    });

    testWidgets('cannot be approved from the screen, but can still be denied', (tester) async {
      tester.view.physicalSize = const Size(400, 2000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final h = Harness(vectorRequest10(name));
      await tester.pumpWidget(MaterialApp(
        theme: buildTheme(Brightness.light),
        home: ApprovalScreen(
            services: h.services,
            authReqId: h.oidc.request['auth_req_id'] as String),
      ));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('request-unsupported')), findsOneWidget);
      final approve =
          tester.widget<FilledButton>(find.byKey(const Key('approve')));
      expect(approve.onPressed, isNull);
      expect(h.oidc.receipts, isEmpty);
      // Refusing is always safe: the deny button stays on.
      final deny = tester.widget<OutlinedButton>(find.byKey(const Key('deny')));
      expect(deny.onPressed, isNotNull);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
