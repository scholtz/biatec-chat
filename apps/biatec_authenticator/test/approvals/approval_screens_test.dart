import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/approvals/widgets/expiry.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'approval_test.dart' show Harness, vectorRequest;

const _stepUp = 'stepup-transaction-approve';

Future<void> _pumpApproval(WidgetTester tester, Harness h,
    {Size size = const Size(400, 2000),
    Brightness brightness = Brightness.light,
    double scale = 1}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearAllTestValues);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: largeTypeBuilder,
    home: ApprovalScreen(
        services: h.services,
        authReqId: h.oidc.request['auth_req_id'] as String),
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _pumpPending(WidgetTester tester, Harness h,
    {Size size = const Size(400, 900),
    Brightness brightness = Brightness.light,
    double scale = 1}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearAllTestValues);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: largeTypeBuilder,
    home: PendingScreen(
        services: h.services, pollEvery: const Duration(hours: 1)),
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _end(WidgetTester tester) => tester.pumpWidget(const SizedBox());

Future<void> _code(WidgetTester tester, String code) async {
  await tester.ensureVisible(find.byKey(const Key('binding-code')));
  await tester.enterText(find.byKey(const Key('binding-code')), code);
}

bool _enabled(WidgetTester tester, String key) {
  final w = tester.widget(find.byKey(Key(key)));
  return switch (w) {
    FilledButton(:final onPressed) => onPressed != null,
    OutlinedButton(:final onPressed) => onPressed != null,
    _ => throw StateError('not a button: $key'),
  };
}

void main() {
  group('the pending list', () {
    testWidgets('shows each request as a card: client, amount, time left, ring',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpPending(tester, h);
      final request = ConsentRequest.fromJson(h.oidc.request);
      final d = request.detail as TransactionDetail;
      expect(find.byKey(Key('pending-${request.authReqId}')), findsOneWidget);
      expect(find.text('${d.amount} ${d.assetSymbol}'), findsOneWidget);
      expect(find.textContaining(d.network), findsWidgets);
      // The time left, in words (the vectors give a request 280 seconds).
      expect(find.text('4 min left'), findsOneWidget);
      expect(find.byType(ExpiryRing), findsOneWidget);
      await _end(tester);
    });

    testWidgets('the time left is redrawn as it runs out', (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpPending(tester, h);
      expect(find.text('4 min left'), findsOneWidget);
      h.now = h.now.add(const Duration(minutes: 3));
      await tester.pump(const Duration(seconds: 31));
      expect(find.text('1 min left'), findsOneWidget);
      h.now = h.now.add(const Duration(seconds: 70));
      await tester.pump(const Duration(seconds: 31));
      expect(find.text('Less than a minute left'), findsOneWidget);
      await _end(tester);
    });

    testWidgets(
        'a card is one button for a screen reader, with everything in full',
        (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest(_stepUp));
      await _pumpPending(tester, h);
      final d =
          ConsentRequest.fromJson(h.oidc.request).detail as TransactionDetail;
      expect(
          find.bySemanticsLabel(RegExp(
              'Request from .*${RegExp.escape('${d.amount} ${d.assetSymbol}')}.*expires in 4 minutes.*Double tap to open')),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets(
        'an unverified client is marked in words, not by the icon alone',
        (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest('direct-transaction-approve-unverified'));
      await _pumpPending(tester, h);
      expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp('Unverified application')),
          findsWidgets);
      handle.dispose();
      await _end(tester);
    });

    for (final brightness in Brightness.values) {
      testWidgets(
          'the list holds at 3x text on a small screen (${brightness.name})',
          (tester) async {
        final h = Harness(vectorRequest(_stepUp));
        await _pumpPending(tester, h,
            size: const Size(320, 568), brightness: brightness, scale: 3);
        expect(tester.takeException(), isNull);
        expect(find.byKey(Key('pending-${h.oidc.request['auth_req_id']}')),
            findsOneWidget);
        await _end(tester);
      });
    }

    testWidgets('every control is at least 48 dp and labelled', (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest(_stepUp));
      await _pumpPending(tester, h);
      await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
      await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
      handle.dispose();
      await _end(tester);
    });

    testWidgets('opens the request when the card is tapped', (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpPending(tester, h);
      await tester
          .tap(find.byKey(Key('pending-${h.oidc.request['auth_req_id']}')));
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsOneWidget);
      await _end(tester);
    });

    testWidgets('loading, empty, offline and error each look like themselves',
        (tester) async {
      // Loading: before the first answer.
      final loading = Harness(vectorRequest(_stepUp));
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home: PendingScreen(
              services: loading.services,
              pollEvery: const Duration(hours: 1))));
      expect(find.byType(LoadingSkeleton), findsOneWidget);
      await tester.pump();
      await tester.pump();
      await _end(tester);

      // Empty: nothing is pending.
      final empty = Harness(vectorRequest(_stepUp))..oidc.status = 'approved';
      await _pumpPending(tester, empty);
      expect(find.text('No pending approvals'), findsOneWidget);
      await _end(tester);

      // Offline: the first load cannot reach the server.
      final offline = Harness(vectorRequest(_stepUp))..oidc.offline = true;
      await _pumpPending(tester, offline);
      expect(find.byKey(const Key('pending-error')), findsOneWidget);
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.text('Try again'), findsOneWidget);
      await _end(tester);
    });
  });

  group('the approval card', () {
    testWidgets('the amount is the hero, the recipient comes before the sender',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      final d =
          ConsentRequest.fromJson(h.oidc.request).detail as TransactionDetail;
      final amount = tester.widget<Text>(find.byKey(const Key('fact-amount')));
      expect(amount.data, '${d.amount} ${d.assetSymbol}');
      final body =
          Theme.of(tester.element(find.byKey(const Key('fact-amount'))))
              .textTheme
              .bodyMedium!
              .fontSize!;
      expect(amount.style!.fontSize, greaterThan(body * 1.5));
      expect(find.text('You are approving'), findsOneWidget);
      if (d.receiver != null) {
        expect(
            tester.getTopLeft(find.byKey(const Key('fact-receiver'))).dy,
            lessThan(
                tester.getTopLeft(find.byKey(const Key('fact-sender'))).dy));
      }
      await _end(tester);
    });

    testWidgets('the amount, recipient and client are announced in full',
        (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest('direct-transaction-approve-verified'));
      await _pumpApproval(tester, h);
      final request = ConsentRequest.fromJson(h.oidc.request);
      final d = request.detail as TransactionDetail;
      expect(
          find.bySemanticsLabel(
              'You are approving ${d.amount} ${d.assetSymbol}, worth ${d.fiatAmount} ${d.fiatCurrency}'),
          findsOneWidget);
      // The label and the whole address are one item.
      final to = tester.getSemantics(find.byKey(const Key('fact-receiver')));
      expect('${to.label} ${to.value}', contains('To'));
      expect('${to.label} ${to.value}', contains(d.receiver!));
      expect(
          find.bySemanticsLabel(RegExp(
              '${RegExp.escape(request.client.displayName!)}[\\s\\S]*Verified')),
          findsWidgets);
      expect(
          find.bySemanticsLabel(RegExp('Request expires in')), findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('the binding code is a large fixed-width field with its reason',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      final field =
          tester.widget<TextField>(find.byKey(const Key('binding-code')));
      expect(field.style!.fontFamily, 'monospace');
      expect(field.style!.fontSize, greaterThan(20));
      expect(find.text('Code shown where you started this action'),
          findsOneWidget);
      expect(find.textContaining('must match the screen where you started'),
          findsOneWidget);
      expect(find.textContaining('Never enter a code that someone sends you'),
          findsOneWidget);
      expect(find.text('Three attempts in total'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('an unverified client is a warning with an icon and words',
        (tester) async {
      final h = Harness(vectorRequest('direct-transaction-approve-unverified'));
      await _pumpApproval(tester, h);
      final banner = find.byKey(const Key('client-unverified'));
      expect(banner, findsOneWidget);
      expect(
          find.descendant(
              of: banner, matching: find.byIcon(Icons.warning_amber_rounded)),
          findsOneWidget);
      expect(
          find.descendant(
              of: banner,
              matching: find.textContaining('Unverified application')),
          findsOneWidget);
      expect(
          find.descendant(
              of: banner,
              matching: find.textContaining('started this action yourself')),
          findsOneWidget);
      await _end(tester);
    });

    testWidgets('a limits change says in words whether it raises or lowers',
        (tester) async {
      final h = Harness(vectorRequest('stepup-limits-change-approve'));
      await _pumpApproval(tester, h);
      final d =
          ConsentRequest.fromJson(h.oidc.request).detail as LimitsChangeDetail;
      final raises = ApprovalCard.raisesLimits(d);
      expect(find.byKey(Key(raises ? 'limits-raised' : 'limits-lowered')),
          findsOneWidget);
      expect(find.byKey(Key(raises ? 'limits-lowered' : 'limits-raised')),
          findsNothing);
      expect(
          find.text(
              raises ? 'This raises your limits' : 'This lowers your limits'),
          findsOneWidget);
      await _end(tester);
    });

    test('a limit that goes up, or cannot be read, counts as raising', () {
      LimitsChangeDetail limits(String a, String b, String c, String d) =>
          LimitsChangeDetail(
              current: Limits(perTransactionEur: a, perDayEur: b),
              requested: Limits(perTransactionEur: c, perDayEur: d),
              requestHash: 'h');
      expect(ApprovalCard.raisesLimits(limits('100', '500', '200', '500')),
          isTrue);
      expect(ApprovalCard.raisesLimits(limits('100', '500', '100', '600')),
          isTrue);
      expect(ApprovalCard.raisesLimits(limits('100', '500', '50', '400')),
          isFalse);
      expect(ApprovalCard.raisesLimits(limits('100', '500', '100', '500')),
          isFalse);
      expect(ApprovalCard.raisesLimits(limits('100', '500', 'many', '500')),
          isTrue);
    });
  });

  group('the approval screen', () {
    testWidgets('keeps Approve and Deny in a bar at the bottom',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      final view = tester.view.physicalSize;
      for (final key in ['approve', 'deny']) {
        expect(tester.getBottomLeft(find.byKey(Key(key))).dy,
            greaterThan(view.height * 0.8),
            reason: '$key is at the bottom');
      }
      // The bar does not scroll with the card.
      final before = tester.getTopLeft(find.byKey(const Key('approve')));
      await tester.drag(find.byType(ListView), const Offset(0, -300));
      await tester.pump();
      expect(tester.getTopLeft(find.byKey(const Key('approve'))), before);
      expect(find.text('Deny'), findsOneWidget);
      expect(find.text('Approve'), findsOneWidget);
      await _end(tester);
    });

    for (final brightness in Brightness.values) {
      testWidgets(
          'the bar stays visible and nothing overflows at 3x text (${brightness.name})',
          (tester) async {
        final h = Harness(vectorRequest(_stepUp));
        await _pumpApproval(tester, h,
            size: const Size(360, 640), brightness: brightness, scale: 3);
        expect(tester.takeException(), isNull);
        final screen = Offset.zero & tester.view.physicalSize;
        for (final key in ['approve', 'deny']) {
          expect(
              screen.contains(tester.getCenter(find.byKey(Key(key)))), isTrue,
              reason: '$key is on screen');
        }
        // Stacked at this size: Approve above Deny, both at least 48 dp high.
        expect(tester.getTopLeft(find.byKey(const Key('approve'))).dy,
            lessThan(tester.getTopLeft(find.byKey(const Key('deny'))).dy));
        for (final key in ['approve', 'deny']) {
          expect(tester.getSize(find.byKey(Key(key))).height,
              greaterThanOrEqualTo(48));
        }
        // The card and the field are still reachable by scrolling.
        await tester.scrollUntilVisible(
            find.byKey(const Key('binding-code')), 200,
            scrollable: find
                .descendant(
                    of: find.byType(ListView),
                    matching: find.byType(Scrollable))
                .first);
        await tester.pump();
        expect(tester.takeException(), isNull);
        await _end(tester);
      });
    }

    testWidgets('every control is at least 48 dp and labelled', (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
      await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
      handle.dispose();
      await _end(tester);
    });

    testWidgets(
        'offline: a banner with the reason, and both buttons are off until a live fetch works',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      expect(_enabled(tester, 'approve'), isTrue);
      expect(_enabled(tester, 'deny'), isTrue);

      h.oidc.offline = true;
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.textContaining('Offline. Nothing was approved.'),
          findsOneWidget);
      expect(_enabled(tester, 'approve'), isFalse);
      expect(_enabled(tester, 'deny'), isFalse);
      expect(find.byKey(const Key('actions-reason')), findsOneWidget);
      expect(find.textContaining('off until Biatec OIDC can be reached'),
          findsOneWidget);

      // Back online: a live fetch turns them on again.
      h.oidc.offline = false;
      await tester.tap(find.text('Try again'));
      await tester.pump();
      await tester.pump();
      expect(find.text('You are offline'), findsNothing);
      expect(_enabled(tester, 'approve'), isTrue);
      expect(_enabled(tester, 'deny'), isTrue);
      expect(find.byKey(const Key('actions-reason')), findsNothing);
      await _end(tester);
    });

    testWidgets(
        'a request that runs out while it is open turns both buttons off',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      expect(find.byKey(const Key('expires-in')), findsOneWidget);
      h.now = ConsentRequest.fromJson(h.oidc.request)
          .expiresAt
          .add(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('Expired'), findsOneWidget);
      expect(_enabled(tester, 'approve'), isFalse);
      expect(_enabled(tester, 'deny'), isFalse);
      expect(
          find.textContaining(
              'Expired: this request can no longer be approved'),
          findsOneWidget);
      await _end(tester);
    });

    testWidgets('the time left is words and a ring', (tester) async {
      final handle = tester.ensureSemantics();
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      expect(find.text('Expires in 4:40'), findsOneWidget);
      expect(find.byType(ExpiryRing), findsOneWidget);
      expect(find.bySemanticsLabel('Request expires in 4 minutes'),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('an expired request is its own screen with a way back',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      h.now = ConsentRequest.fromJson(h.oidc.request)
          .expiresAt
          .add(const Duration(seconds: 5));
      await _pumpApproval(tester, h);
      expect(find.text('This request has expired.'), findsOneWidget);
      expect(find.text('Back to approvals'), findsOneWidget);
      expect(find.byKey(const Key('approve')), findsNothing);
      await _end(tester);
    });

    testWidgets('an already decided request says so and offers the way back',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp))..oidc.status = 'approved';
      await tester.pumpWidget(MaterialApp(
          home: Builder(
              builder: (context) => Scaffold(
                  body: FilledButton(
                      onPressed: () => Navigator.of(context).push(
                          ApprovalScreen.route(h.services,
                              h.oidc.request['auth_req_id'] as String)),
                      child: const Text('open'))))));
      await tester.tap(find.text('open'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('Already decided'), findsOneWidget);
      expect(find.textContaining('already decided'), findsWidgets);
      await tester.tap(find.text('Back to approvals'));
      await tester.pumpAndSettle();
      expect(find.byType(ApprovalScreen), findsNothing);
    });

    testWidgets('a request that cannot be loaded offers to try again',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp))..oidc.offline = true;
      await _pumpApproval(tester, h);
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.text('Offline. Approvals need a connection to Biatec OIDC.'),
          findsOneWidget);
      h.oidc.offline = false;
      await tester.tap(find.text('Try again'));
      await tester.pump();
      await tester.pump();
      expect(find.byType(ApprovalCard), findsOneWidget);
      await _end(tester);
    });

    testWidgets('loading shows a skeleton, not a bare spinner', (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home: ApprovalScreen(
              services: h.services,
              authReqId: h.oidc.request['auth_req_id'] as String)));
      expect(find.byKey(const Key('approval-loading')), findsOneWidget);
      expect(find.byKey(const Key('approve')), findsNothing);
      await tester.pump();
      await tester.pump();
      await _end(tester);
    });

    testWidgets('approving ends on a result with a way to leave',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Approved.'), findsOneWidget);
      expect(find.text('Done'), findsOneWidget);
      expect(find.byKey(const Key('approve')), findsNothing);
      await _end(tester);
    });

    testWidgets('denying ends on a result too, with no code needed',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await tester.tap(find.byKey(const Key('deny')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Denied.'), findsOneWidget);
      expect(find.text('Done'), findsOneWidget);
      await _end(tester);
    });

    testWidgets(
        'a wrong code says so and how many attempts are left; the third ends it',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await _code(tester, '000000');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Wrong code'), findsOneWidget);
      expect(
          find.textContaining('Wrong code. 2 attempts left.'), findsOneWidget);
      expect(find.text('2 attempts left', skipOffstage: false), findsOneWidget);
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.text('Three wrong codes. The request was refused.'),
          findsOneWidget);
      expect(find.text('Back to approvals'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a code that is not six digits is told so, nothing is sent',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await _code(tester, '12');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      expect(find.text('Could not complete'), findsOneWidget);
      expect(find.textContaining('Enter the six-digit code.'), findsOneWidget);
      expect(h.oidc.receipts, isEmpty);
      await _end(tester);
    });

    testWidgets('a lost answer after posting is a banner, not a result',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp))..oidc.dropAnswers = true;
      await _pumpApproval(tester, h);
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('approval-error')), findsOneWidget);
      expect(find.text('Approved.'), findsNothing);
      expect(find.byType(ApprovalCard), findsOneWidget);
      await _end(tester);
    });
  });
}
