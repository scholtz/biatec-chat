import 'dart:async';

import 'package:biatec_authenticator/approvals/approval_card.dart';
import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/consent_request.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/approvals/widgets/approval_actions.dart';
import 'package:biatec_authenticator/approvals/widgets/expiry.dart';
import 'package:biatec_authenticator/approvals/widgets/mono.dart';
import 'package:biatec_authenticator/security/touch_guard.dart';
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
    double scale = 1,
    ValueChanged<int>? onCount}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearAllTestValues);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: largeTypeBuilder,
    home: PendingScreen(
        services: h.services,
        pollEvery: const Duration(hours: 1),
        onCount: onCount),
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _end(WidgetTester tester) => tester.pumpWidget(const SizedBox());

/// Scrolls the list until the code field exists and is on screen (it is built lazily at large text).
Future<void> _toCode(WidgetTester tester) =>
    tester.scrollUntilVisible(find.byKey(const Key('binding-code')), 200,
        scrollable: find
            .descendant(
                of: find.byType(ListView), matching: find.byType(Scrollable))
            .first);

Future<void> _code(WidgetTester tester, String code) async {
  await _toCode(tester);
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

class _FlagGuard implements TouchGuard {
  bool obscured = false;
  int asked = 0;

  @override
  Future<bool> partiallyObscured() async {
    asked++;
    return obscured;
  }
}

class _SlowGuard implements TouchGuard {
  final release = Completer<void>();
  int asked = 0;

  @override
  Future<bool> partiallyObscured() async {
    asked++;
    await release.future;
    return false;
  }
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
      expect(find.text(d.headline), findsOneWidget);
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
              'Request from .*${RegExp.escape(d.headline)}.*expires in 4 minutes.*Double tap to open')),
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
      expect(
          find.bySemanticsLabel(RegExp('Unproven application')), findsWidgets);
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

    testWidgets('a request that runs out leaves the list without a refresh',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      final counts = <int>[];
      await _pumpPending(tester, h, onCount: counts.add);
      expect(counts.last, 1);
      expect(find.byKey(Key('pending-${h.oidc.request['auth_req_id']}')),
          findsOneWidget);
      h.now = ConsentRequest.fromJson(h.oidc.request)
          .expiresAt
          .add(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 11));
      expect(find.byKey(Key('pending-${h.oidc.request['auth_req_id']}')),
          findsNothing);
      expect(counts.last, 0, reason: 'the badge follows');
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
      expect(amount.data, d.headline);
      final body =
          Theme.of(tester.element(find.byKey(const Key('fact-amount'))))
              .textTheme
              .bodyMedium!
              .fontSize!;
      expect(amount.style!.fontSize, greaterThan(body * 1.5));
      expect(find.text('You are approving'), findsOneWidget);
      expect(tester.getTopLeft(find.byKey(const Key('transfer-0-receiver'))).dy,
          lessThan(tester.getTopLeft(find.byKey(const Key('fact-sender'))).dy));
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
              'You are approving ${d.headline}, worth ${d.valueText}'),
          findsOneWidget);
      // The label and the whole address are one item.
      final to =
          tester.getSemantics(find.byKey(const Key('transfer-0-receiver')));
      expect('${to.label} ${to.value}', contains('To'));
      expect('${to.label} ${to.value}',
          contains(d.group!.transfers.single.receiver));
      expect(
          find.bySemanticsLabel(RegExp(
              '${RegExp.escape(request.client.domain!)}[\\s\\S]*Proven domain')),
          findsWidgets);
      expect(
          find.bySemanticsLabel(RegExp('Request expires in')), findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    test('the ring is sized by the request window, however the number comes',
        () {
      ConsentRequest with_(Object? window) {
        final json = {...vectorRequest(_stepUp)};
        if (window == null) {
          json.remove('requested_expiry');
        } else {
          json['requested_expiry'] = window;
        }
        return ConsentRequest.fromJson(json);
      }

      expect(requestWindowSeconds(with_(600)), 600);
      expect(requestWindowSeconds(with_(600.0)), 600);
      expect(requestWindowSeconds(with_(null)), 300);
      expect(requestWindowSeconds(with_('300')), 300);
      expect(requestWindowSeconds(with_(0)), 300);
      final r = with_(600);
      expect(expiryFraction(r, r.expiresAt), 0);
      expect(
          expiryFraction(r, r.expiresAt.subtract(const Duration(seconds: 300))),
          closeTo(0.5, 1e-9));
      expect(expiryFraction(r, r.expiresAt.add(const Duration(hours: 1))), 0);
      expect(
          expiryFraction(r, r.expiresAt.subtract(const Duration(days: 1))), 1);
    });

    test('the fixed-width face has fallbacks for platforms without monospace',
        () {
      expect(monoStyle.fontFamily, 'monospace');
      expect(monoStyle.fontFamilyFallback, containsAll(['Menlo', 'Consolas']));
    });

    testWidgets('addresses and hashes use the fixed-width face',
        (tester) async {
      final h = Harness(vectorRequest('direct-transaction-approve-verified'));
      await _pumpApproval(tester, h);
      for (final key in ['transfer-0-receiver', 'fact-sender', 'fact-hash']) {
        final text = tester.widget<SelectableText>(find.descendant(
            of: find.byKey(Key(key)), matching: find.byType(SelectableText)));
        expect(text.style!.fontFamily, 'monospace', reason: key);
        expect(text.style!.fontFamilyFallback, contains('Menlo'), reason: key);
      }
      // Plain words are not.
      final network = tester.widget<SelectableText>(find.descendant(
          of: find.byKey(const Key('fact-network')),
          matching: find.byType(SelectableText)));
      expect(network.style, isNull);
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
              matching: find.textContaining('Unproven application')),
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
      final raises = ApprovalCard.limitsMove(d) == LimitsMove.raises;
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

    test('equal limits are neither raised nor lowered', () {
      LimitsChangeDetail limits(String a, String b, String c, String d) =>
          LimitsChangeDetail(
              current: Limits(perTransactionEur: a, perDayEur: b),
              requested: Limits(perTransactionEur: c, perDayEur: d),
              requestHash: 'h');
      expect(ApprovalCard.limitsMove(limits('100', '500', '100', '500')),
          LimitsMove.same);
      expect(ApprovalCard.limitsMove(limits('100', '500', '50', '500')),
          LimitsMove.lowers);
      expect(ApprovalCard.limitsMove(limits('100', '500', '50', '600')),
          LimitsMove.raises,
          reason: 'one up is raising, whatever else goes down');
      expect(ApprovalCard.limitsMove(limits('100', '500', 'x', '500')),
          LimitsMove.raises);
    });

    testWidgets('equal limits say so, not that they are lowered',
        (tester) async {
      final json = vectorRequest('stepup-limits-change-approve');
      final detail = ((json['authorization_details'] as List).single as Map)
          .cast<String, Object?>();
      detail['requested_limits'] = detail['current_limits'];
      final h = Harness(json);
      await _pumpApproval(tester, h);
      expect(find.byKey(const Key('limits-same')), findsOneWidget);
      expect(find.text('These limits stay the same'), findsOneWidget);
      expect(find.byKey(const Key('limits-lowered')), findsNothing);
      await _end(tester);
    });

    test('a limit that goes up, or cannot be read, counts as raising', () {
      LimitsChangeDetail limits(String a, String b, String c, String d) =>
          LimitsChangeDetail(
              current: Limits(perTransactionEur: a, perDayEur: b),
              requested: Limits(perTransactionEur: c, perDayEur: d),
              requestHash: 'h');
      expect(
          (ApprovalCard.limitsMove(limits('100', '500', '200', '500')) ==
              LimitsMove.raises),
          isTrue);
      expect(
          (ApprovalCard.limitsMove(limits('100', '500', '100', '600')) ==
              LimitsMove.raises),
          isTrue);
      expect(
          (ApprovalCard.limitsMove(limits('100', '500', '50', '400')) ==
              LimitsMove.raises),
          isFalse);
      expect(
          (ApprovalCard.limitsMove(limits('100', '500', '100', '500')) ==
              LimitsMove.raises),
          isFalse);
      expect(
          (ApprovalCard.limitsMove(limits('100', '500', 'many', '500')) ==
              LimitsMove.raises),
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

    testWidgets(
        'with the keyboard up, and the offline banner, both buttons stay and nothing overflows',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h, size: const Size(360, 640), scale: 3);
      h.oidc.offline = true;
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(tester.takeException(), isNull, reason: 'offline at 3x text');
      final barHeight = tester.getSize(find.byType(ApprovalActionBar)).height;

      // The keyboard opens: the bar is no taller, the buttons stay, the card scrolls in what is left.
      tester.view.viewInsets = const FakeViewPadding(bottom: 250);
      addTearDown(tester.view.resetViewInsets);
      await tester.pump();
      expect(tester.takeException(), isNull, reason: 'keyboard at 3x text');
      expect(find.byKey(const Key('approve')), findsOneWidget);
      expect(find.byKey(const Key('deny')), findsOneWidget);
      expect(tester.getSize(find.byType(ApprovalActionBar)).height,
          lessThanOrEqualTo(barHeight));
      await _end(tester);
    });

    testWidgets(
        'the bar never takes more than half of a small screen at 3x text',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h, size: const Size(360, 640), scale: 3);
      expect(tester.getSize(find.byType(ApprovalActionBar)).height,
          lessThan(640 / 2));
      await _end(tester);
    });

    testWidgets('scrolling the card closes the keyboard', (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h, size: const Size(400, 700));
      final list = tester.widget<ListView>(find.byType(ListView));
      expect(list.keyboardDismissBehavior,
          ScrollViewKeyboardDismissBehavior.onDrag);
      await _end(tester);
    });

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
      expect(find.textContaining('off until Biatec OIDC can be reached'),
          findsNothing);
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
      expect(find.byKey(const Key('approval-expired')), findsOneWidget);
      expect(find.text('This request has expired'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('at exactly zero seconds left it is expired, not a blank state',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      h.now = ConsentRequest.fromJson(h.oidc.request).expiresAt;
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('Expired'), findsOneWidget);
      expect(find.byKey(const Key('approval-expired')), findsOneWidget);
      expect(_enabled(tester, 'approve'), isFalse);
      await _end(tester);
    });

    testWidgets('the bar sees the keyboard that Scaffold lifts it above',
        (tester) async {
      Future<void> pump() => tester.pumpWidget(MaterialApp(
          home: Scaffold(
              body: const SizedBox.expand(),
              bottomNavigationBar: ApprovalActionBar(
                  busy: true, onApprove: () {}, onDeny: () {}))));
      tester.view.physicalSize = const Size(360, 640);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await pump();
      expect(find.byType(LinearProgressIndicator), findsOneWidget);
      tester.view.viewInsets = const FakeViewPadding(bottom: 250);
      addTearDown(tester.view.resetViewInsets);
      await tester.pump();
      expect(find.byType(LinearProgressIndicator), findsNothing,
          reason: 'the bar knew the keyboard was up');
      expect(find.byKey(const Key('approve')), findsOneWidget);
    });

    testWidgets('denying says what the app knows: it sent the decision',
        (tester) async {
      final h = Harness(vectorRequest(_stepUp));
      await _pumpApproval(tester, h);
      await tester.tap(find.byKey(const Key('deny')));
      await tester.pump();
      await tester.pump();
      expect(
          find.text(
              'Nothing was approved. Your decision was sent to Biatec OIDC.'),
          findsOneWidget);
      expect(find.textContaining('requester is told'), findsNothing);
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

    testWidgets(
        'a card under a stopped TickerMode does not redraw every second',
        (tester) async {
      Future<bool> redraws(bool enabled) async {
        final h = Harness(vectorRequest(_stepUp));
        tester.view.physicalSize = const Size(400, 900);
        tester.view.devicePixelRatio = 1.0;
        addTearDown(tester.view.reset);
        await tester.pumpWidget(MaterialApp(
            home: TickerMode(
                enabled: enabled,
                child: ApprovalScreen(
                    services: h.services,
                    authReqId: h.oidc.request['auth_req_id'] as String))));
        await tester.pump();
        await tester.pump();
        // The screen's own Scaffold is rebuilt only by the screen's own redraw.
        var scaffoldBuilds = 0;
        debugOnRebuildDirtyWidget = (element, builtOnce) {
          if (element.widget is Scaffold) scaffoldBuilds++;
        };
        addTearDown(() => debugOnRebuildDirtyWidget = null);
        await tester.pump(const Duration(milliseconds: 1100));
        debugOnRebuildDirtyWidget = null;
        await tester.pumpWidget(const SizedBox());
        return scaffoldBuilds > 0;
      }

      expect(await redraws(true), isTrue, reason: 'a visible card ticks');
      expect(await redraws(false), isFalse, reason: 'a hidden card does not');
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

    testWidgets(
        'a tap that was partly covered by another app sends nothing and says why',
        (tester) async {
      final guard = _FlagGuard()..obscured = true;
      final h = Harness(vectorRequest(_stepUp), touchGuard: guard);
      await _pumpApproval(tester, h);
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('touch-obscured')), findsOneWidget);
      expect(h.gate.asked, 0, reason: 'no biometric, no signing');
      expect(find.text('Approved.'), findsNothing);
      expect(guard.asked, 1);

      // Closing the overlay and tapping again goes through.
      guard.obscured = false;
      await tester.pump(const Duration(seconds: 5));
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Approved.'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a second tap while the check runs starts no second approval',
        (tester) async {
      final guard = _SlowGuard();
      final h = Harness(vectorRequest(_stepUp), touchGuard: guard);
      await _pumpApproval(tester, h);
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      expect(guard.asked, 1, reason: 'the second tap is ignored');
      guard.release.complete();
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(h.keys.prompts, hasLength(1), reason: 'one biometric, one approval');
      expect(h.gate.asked, 0);
      expect(find.text('Approved.'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a request that ran out during the check is not approved',
        (tester) async {
      final guard = _SlowGuard();
      final h = Harness(vectorRequest(_stepUp), touchGuard: guard);
      await _pumpApproval(tester, h);
      await _code(tester, '483920');
      await tester.tap(find.byKey(const Key('approve')));
      await tester.pump();
      h.now = h.now.add(const Duration(hours: 1));
      guard.release.complete();
      await tester.pump();
      await tester.pump();
      expect(h.gate.asked, 0, reason: 'expired while the platform answered');
      expect(find.text('Approved.'), findsNothing);
      await _end(tester);
    });

    testWidgets('denying is not held up by the check', (tester) async {
      final h = Harness(vectorRequest(_stepUp),
          touchGuard: _FlagGuard()..obscured = true);
      await _pumpApproval(tester, h);
      await tester.tap(find.byKey(const Key('deny')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Denied.'), findsOneWidget);
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
