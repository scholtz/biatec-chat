import 'package:biatec_authenticator/codes/codes_screen.dart';
import 'package:biatec_authenticator/codes/widgets/code_tile.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/shell/app_lock_gate.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_generator.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import '../totp/vault/support.dart' show account;
import 'support.dart';

/// 10:00:00 on a Monday, a minute that begins on a period boundary for 30 and 15 seconds alike.
final base = DateTime.utc(2026, 11, 2, 10, 0, 0);

Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 4; i++) {
    await tester.pump(const Duration(milliseconds: 20));
  }
}

Future<void> pumpCodes(WidgetTester tester, CodesRig rig,
    {ValueListenable<bool>? active,
    Size size = const Size(400, 900),
    double text = 1,
    bool dark = false,
    bool accessible = false}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(dark ? Brightness.dark : Brightness.light),
    builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(accessibleNavigation: accessible),
        child: child!),
    home: CodesScreen(services: rig.services, active: active),
  ));
  await settle(tester);
}

Future<void> leave(WidgetTester tester) async {
  await tester.pumpWidget(const SizedBox());
}

String codeNow(CodesRig rig, OtpAccount a) =>
    OtpGenerator.group(OtpGenerator.code(a, rig.now));

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('empty', () {
    testWidgets('offers the three ways to add', (tester) async {
      final rig = await CodesRig.create(now: base);
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('codes-empty')), findsOneWidget);
      for (final k in ['empty-scan', 'empty-link', 'empty-key']) {
        expect(find.byKey(Key(k)), findsOneWidget, reason: k);
      }
      await tester.tap(find.byKey(const Key('empty-key')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('key-secret')), findsOneWidget);
      await leave(tester);
    });
  });

  group('the list', () {
    testWidgets(
        'shows issuer, name and the code in groups, a ring and the seconds',
        (tester) async {
      final rig =
          await CodesRig.create(now: base.add(const Duration(seconds: 7)));
      final a = account(issuer: 'GitHub', label: 'alice@example.com', seed: 1);
      final expected = codeNow(rig, a);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      expect(find.text('GitHub'), findsOneWidget);
      expect(find.text('alice@example.com'), findsOneWidget);
      expect(find.byKey(Key('code-text-$id')), findsOneWidget);
      expect(
          tester.widget<Text>(find.byKey(Key('code-text-$id'))).data, expected);
      expect(expected, matches(RegExp(r'^\d{3} \d{3}$')));
      // The seconds are written, not only drawn.
      expect(
          find.descendant(
              of: find.byKey(Key('ring-$id')), matching: find.text('23')),
          findsOneWidget);
      expect(find.byType(CircularProgressIndicator), findsWidgets);
      await leave(tester);
    });

    testWidgets('codes change on the period boundary and never drift',
        (tester) async {
      final rig = await CodesRig.create(
          now: base.add(const Duration(milliseconds: 25400)));
      final a = account(seed: 1);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      final seen = <String>{};
      // Three periods, second by second: the screen always shows what the engine says for the clock.
      for (var i = 0; i < 95; i++) {
        rig.advance(const Duration(seconds: 1));
        await tester.pump(const Duration(seconds: 1));
        final shown =
            tester.widget<Text>(find.byKey(Key('code-text-$id'))).data;
        expect(shown, codeNow(rig, a), reason: 'second $i at ${rig.now}');
        seen.add(shown!);
      }
      expect(seen.length, greaterThanOrEqualTo(3));
      await leave(tester);
    });

    testWidgets('the next code appears in the last five seconds only',
        (tester) async {
      final rig =
          await CodesRig.create(now: base.add(const Duration(seconds: 20)));
      final a = account(seed: 1);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      expect(find.byKey(Key('next-$id')), findsNothing);
      rig.now = base.add(const Duration(seconds: 25, milliseconds: 100));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byKey(Key('next-$id')), findsOneWidget);
      final next = OtpGenerator.group(OtpGenerator.nextCode(a, rig.now));
      expect(find.text('Next: $next'), findsOneWidget);
      await leave(tester);
    });

    testWidgets('a counter-based account has a button for its next code',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(type: OtpType.hotp, counter: 3, seed: 1));
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      final before = tester.widget<Text>(find.byKey(Key('code-text-$id'))).data;
      await tester.tap(find.byKey(Key('counter-$id')));
      await settle(tester);
      final after = tester.widget<Text>(find.byKey(Key('code-text-$id'))).data;
      expect(after, isNot(before));
      expect((await rig.vault.list()).single.counter, 4);
      await leave(tester);
    });

    testWidgets('search narrows the list', (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(issuer: 'GitHub', seed: 1));
      await rig.addAccount(account(issuer: 'Bank', seed: 2));
      await pumpCodes(tester, rig);
      await tester.tap(find.byKey(const Key('search')));
      await tester.pump();
      await tester.enterText(find.byKey(const Key('search-field')), 'bank');
      await settle(tester);
      expect(find.text('Bank'), findsOneWidget);
      expect(find.text('GitHub'), findsNothing);
      await tester.enterText(find.byKey(const Key('search-field')), 'zzz');
      await settle(tester);
      expect(find.byKey(const Key('no-match')), findsOneWidget);
      await leave(tester);
    });

    testWidgets('moving, renaming and deleting persist', (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(issuer: 'A', seed: 1));
      await rig.addAccount(account(issuer: 'B', seed: 2));
      await pumpCodes(tester, rig);
      final ids = [for (final i in await rig.vault.list()) i.id];

      await tester.tap(find.byKey(Key('menu-${ids[0]}')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Move down'));
      await settle(tester);
      expect([for (final i in await rig.vault.list()) i.issuer], ['B', 'A']);

      await tester.tap(find.byKey(Key('menu-${ids[0]}')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Rename'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('rename-issuer')), 'Alpha');
      await tester.tap(find.byKey(const Key('rename-save')));
      await tester.pumpAndSettle();
      expect(find.text('Alpha'), findsOneWidget);
      expect((await rig.vault.list()).last.issuer, 'Alpha');

      // Delete: a confirmation first, then a fresh fingerprint.
      await tester.tap(find.byKey(Key('menu-${ids[0]}')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      expect(find.text('Delete this account?'), findsOneWidget);
      await tester.tap(find.byKey(const Key('keep-account')));
      await tester.pumpAndSettle();
      expect(await rig.vault.list(), hasLength(2));

      await tester.tap(find.byKey(Key('menu-${ids[0]}')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      rig.auth.outcome = BiometricOutcome.failed;
      await tester.tap(find.byKey(const Key('confirm-delete')));
      await tester.pumpAndSettle();
      expect(await rig.vault.list(), hasLength(2),
          reason: 'a refused fingerprint deletes nothing');
      expect(find.textContaining('Nothing was deleted'), findsOneWidget);

      await tester.tap(find.byKey(Key('menu-${ids[0]}')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();
      rig.auth.outcome = BiometricOutcome.success;
      await tester.tap(find.byKey(const Key('confirm-delete')));
      await tester.pumpAndSettle();
      expect(await rig.vault.list(), hasLength(1));
      await leave(tester);
    });
  });

  group('copy and reveal', () {
    testWidgets('tap copies the code to the sensitive clipboard',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      final a = account(seed: 1);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      await tester.tap(find.byKey(Key('copy-$id')));
      await settle(tester);
      expect(rig.clipboard.copied.single, OtpGenerator.code(a, rig.now));
      expect(rig.clipboard.copied.single, isNot(contains(' ')),
          reason: 'the digits, without the groups');
      expect(find.textContaining('cleared from the clipboard in 30 seconds'),
          findsOneWidget);
      await leave(tester);
    });

    testWidgets('tap to reveal hides codes until they are touched',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      final a = account(seed: 1);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      await tester.tap(find.byKey(const Key('tap-to-reveal')));
      await settle(tester);
      expect(rig.prefs.reveal, isTrue);
      expect(tester.widget<Text>(find.byKey(Key('code-text-$id'))).data,
          '••• •••');

      await tester.tap(find.byKey(Key('copy-$id')));
      await settle(tester);
      expect(rig.clipboard.copied, isEmpty,
          reason: 'the first touch only shows it');
      expect(tester.widget<Text>(find.byKey(Key('code-text-$id'))).data,
          codeNow(rig, a));

      rig.advance(const Duration(seconds: 11));
      await tester.pump(const Duration(seconds: 1));
      expect(
          tester.widget<Text>(find.byKey(Key('code-text-$id'))).data, '••• •••',
          reason: 'hidden again');
      await leave(tester);
    });
  });

  group('the ticker', () {
    testWidgets('stops while the tab is hidden and catches up when shown',
        (tester) async {
      final rig =
          await CodesRig.create(now: base.add(const Duration(seconds: 1)));
      final a = account(seed: 1);
      await rig.addAccount(a);
      final active = ValueNotifier(true);
      await pumpCodes(tester, rig, active: active);
      final id = (await rig.vault.list()).single.id;
      active.value = false;
      await tester.pump();
      final frozen = tester.widget<Text>(find.byKey(Key('code-text-$id'))).data;
      rig.advance(const Duration(seconds: 40));
      await tester.pump(const Duration(seconds: 40));
      expect(tester.widget<Text>(find.byKey(Key('code-text-$id'))).data, frozen,
          reason: 'no ticking while hidden');
      active.value = true;
      await tester.pump();
      expect(tester.widget<Text>(find.byKey(Key('code-text-$id'))).data,
          codeNow(rig, a));
      await leave(tester);
    });

    testWidgets('locking the app removes the codes from the tree and stops it',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      final a = account(seed: 1);
      await rig.addAccount(a);
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      rig.auth.outcome = BiometricOutcome.cancelled;
      await tester.pumpWidget(MaterialApp(
          home: AppLockGate(
              lock: rig.lock, child: CodesScreen(services: rig.services))));
      await settle(tester);
      final id = (await rig.vault.list()).single.id;
      expect(find.byKey(Key('code-$id')), findsOneWidget);
      rig.lock.lock();
      await settle(tester);
      expect(find.byType(CodesScreen), findsNothing);
      expect(find.byKey(Key('code-$id')), findsNothing);
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);
      await leave(tester);
    });
  });

  group('screen reader', () {
    testWidgets('hears issuer, name and the code digit by digit in groups',
        (tester) async {
      final semantics = tester.ensureSemantics();
      final rig = await CodesRig.create(now: base);
      final a = account(issuer: 'GitHub', label: 'alice@example.com', seed: 1);
      final code = OtpGenerator.code(a, rig.now);
      await rig.addAccount(a);
      await pumpCodes(tester, rig);
      final id = (await rig.vault.list()).single.id;
      final label = tester.getSemantics(find.byKey(Key('copy-$id'))).label;
      expect(label, contains('GitHub, alice@example.com'));
      expect(label, contains('Code ${spokenCode(code)}'));
      expect(spokenCode('123456'), '1 2 3, 4 5 6');
      expect(spokenCode('1234567'), '1 2 3, 4 5 6, 7');
      expect(label, isNot(contains(RegExp(r'\d{4}'))),
          reason: 'never the digits run together');
      semantics.dispose();
      await leave(tester);
    });

    testWidgets(
        'the countdown is announced at 10 s, 5 s and renewal, not every second',
        (tester) async {
      final rig =
          await CodesRig.create(now: base.add(const Duration(seconds: 1)));
      await rig.addAccount(account(seed: 1));
      final announced = <String>[];
      tester.binding.defaultBinaryMessenger
          .setMockDecodedMessageHandler<dynamic>(SystemChannels.accessibility,
              (message) async {
        final data = (message as Map)['data'];
        if (message['type'] == 'announce' && data is Map) {
          announced.add(data['message'] as String);
        }
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger
          .setMockDecodedMessageHandler<dynamic>(
              SystemChannels.accessibility, null));
      await pumpCodes(tester, rig, accessible: true);
      for (var i = 0; i < 61; i++) {
        rig.advance(const Duration(seconds: 1));
        await tester.pump(const Duration(seconds: 1));
      }
      expect(announced.toSet(), {
        'Codes renew in 10 seconds',
        'Codes renew in 5 seconds',
        'Codes renewed',
      });
      expect(announced.length, lessThanOrEqualTo(7),
          reason: 'about three a period, never one a second');
      await leave(tester);
    });
  });

  group('the clock', () {
    testWidgets('off by more than 5 seconds shows a warning with the fix',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(seed: 1));
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('clock-off')), findsNothing);
      // The server's Date is 40 seconds ahead of this phone.
      final server = base.add(const Duration(seconds: 40));
      rig.serverClock.observe(
          'Mon, 02 Nov 2026 10:00:40 GMT',
          base.subtract(const Duration(milliseconds: 100)),
          base.add(const Duration(milliseconds: 100)));
      expect(rig.serverClock.offset!.inSeconds, inInclusiveRange(39, 41),
          reason: '$server');
      await settle(tester);
      expect(find.byKey(const Key('clock-off')), findsOneWidget);
      expect(find.textContaining('40 seconds behind'), findsOneWidget);
      expect(find.textContaining('automatic date and time'), findsOneWidget);
      await leave(tester);
    });

    testWidgets('a clock within 5 seconds shows nothing', (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(seed: 1));
      rig.serverClock.observe(
          'Mon, 02 Nov 2026 10:00:03 GMT',
          base.subtract(const Duration(milliseconds: 100)),
          base.add(const Duration(milliseconds: 100)));
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('clock-off')), findsNothing);
      await leave(tester);
    });

    testWidgets('a clock reset to the past hides time-based codes and says why',
        (tester) async {
      final rig = await CodesRig.create(now: DateTime.utc(2019, 1, 1));
      await rig.addAccount(account(seed: 1));
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('clock-reset')), findsOneWidget);
      expect(find.byType(CodeTile), findsNothing);
      await leave(tester);
    });

    testWidgets('a clock behind the latest time seen is caught',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      rig.prefs.seen = base.add(const Duration(days: 10));
      await rig.addAccount(account(seed: 1));
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('clock-reset')), findsOneWidget);
      await leave(tester);
    });

    testWidgets(
        'a time saved far ahead does not hide the codes of a correct clock',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      rig.prefs.seen = base.add(const Duration(days: 400));
      await rig.addAccount(account(seed: 1));
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('clock-reset')), findsNothing);
      expect(find.byType(CodeTile), findsOneWidget);
      await leave(tester);
    });
  });

  group('layout', () {
    for (final dark in [false, true]) {
      testWidgets('holds at 3x text, ${dark ? 'dark' : 'light'}',
          (tester) async {
        final semantics = tester.ensureSemantics();
        final rig =
            await CodesRig.create(now: base.add(const Duration(seconds: 27)));
        await rig.addAccount(account(
            issuer: 'A rather long service name',
            label: 'a.long.account.name@example.com',
            seed: 1));
        await rig.addAccount(account(type: OtpType.hotp, seed: 2));
        await pumpCodes(tester, rig,
            size: const Size(320, 4000), text: 3, dark: dark);
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
        await leave(tester);
      });

      testWidgets('empty holds at 3x text, ${dark ? 'dark' : 'light'}',
          (tester) async {
        final semantics = tester.ensureSemantics();
        final rig = await CodesRig.create(now: base);
        await pumpCodes(tester, rig,
            size: const Size(320, 568), text: 3, dark: dark);
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
        await leave(tester);
      });
    }
  });

  group('failed', () {
    testWidgets('a refused fingerprint says so and can be retried',
        (tester) async {
      final rig = await CodesRig.create(now: base);
      await rig.addAccount(account(seed: 1));
      rig.keys.denyAuth = true;
      await pumpCodes(tester, rig);
      expect(find.byKey(const Key('codes-failed')), findsOneWidget);
      expect(find.textContaining('did not pass'), findsOneWidget);
      rig.keys.denyAuth = false;
      await tester.tap(find.text('Try again'));
      await settle(tester);
      expect(find.byType(CodeTile), findsOneWidget);
      await leave(tester);
    });
  });

  test('the lock state enum is the one the Codes tab reacts to', () {
    expect(AppLockState.values, contains(AppLockState.biometricsChanged));
  });
}
