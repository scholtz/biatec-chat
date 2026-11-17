import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/keys/key_texts.dart';
import 'package:biatec_authenticator/l10n/common_texts.dart';
import 'package:biatec_authenticator/l10n/texts.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/lock/lock_texts.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import '../lock/support.dart';
import '../shell/app_shell_test.dart' show ShellRig;
import 'texts_support.dart';

/// A device set to Slovak: its language, and the list the app picks its language from.
void slovakDevice(WidgetTester tester) {
  tester.platformDispatcher.localeTestValue = const Locale('sk');
  tester.platformDispatcher.localesTestValue = const [Locale('sk')];
  addTearDown(tester.platformDispatcher.clearLocaleTestValue);
  addTearDown(tester.platformDispatcher.clearLocalesTestValue);
}

void main() {
  test('every group of the shell, the lock and the key levels has both languages', () {
    expectSameTexts('common', CommonTexts.en.values, CommonTexts.sk.values, sameInBoth: {'offline'});
    expectSameTexts('lock', LockTexts.en.values, LockTexts.sk.values, sameInBoth: {'storeGoogle'});
    expectSameTexts('keys', KeyTexts.en.values, KeyTexts.sk.values);
    final levels = KeySecurityLevel.labelTexts;
    expectSameTexts('keyLevel', levels['en']!, levels['sk']!, sameInBoth: {'secureEnclave'});
    expect(levels['en']!.keys.toSet(), KeySecurityLevel.values.map((l) => l.name).toSet());
  });

  test('the language follows the locale: Slovak for sk, English for anything else', () {
    expect(appLanguageIsSlovak(const Locale('sk')), isTrue);
    expect(appLanguageIsSlovak(const Locale('sk', 'SK')), isTrue);
    for (final other in const [Locale('en'), Locale('cs'), Locale('de')]) {
      expect(appLanguageIsSlovak(other), isFalse, reason: '$other');
    }
    expect(AppTab.codes.labelIn(const Locale('sk')), 'Kódy');
    expect(AppTab.codes.labelIn(const Locale('cs')), 'Codes');
    expect(LockDelay.minute1.labelIn(const Locale('sk')), 'Po 1 minúte');
    expect(KeySecurityLevel.tpm.labelIn(const Locale('sk')), 'bezpečnostný čip TPM');
    expect(CommonTexts.sk.text('pendingCount', {'n': 3}), 'Čakajúce: 3');
  });

  testWidgets('a phone set to Czech, then Slovak, gets Slovak from a controller as from a screen; one set to German, then nothing we have, English', (tester) async {
    tester.platformDispatcher.localesTestValue = const [Locale('cs', 'CZ'), Locale('sk', 'SK')];
    tester.platformDispatcher.localeTestValue = const Locale('cs', 'CZ');
    addTearDown(tester.platformDispatcher.clearLocalesTestValue);
    addTearDown(tester.platformDispatcher.clearLocaleTestValue);
    expect(appLanguageIsSlovak(), isTrue);
    expect(CommonTexts.of().text('tryAgain'), 'Skúsiť znova');
    tester.platformDispatcher.localesTestValue = const [Locale('de')];
    tester.platformDispatcher.localeTestValue = const Locale('de');
    expect(appLanguageIsSlovak(), isFalse);
  });

  group('on a device set to Slovak', () {
    testWidgets('the navigation is Slovak', (tester) async {
      slovakDevice(tester);
      final rig = ShellRig();
      await rig.pump(tester);
      final bar = tester.widget<NavigationBar>(find.byType(NavigationBar));
      expect([for (final d in bar.destinations) (d as NavigationDestination).label],
          ['Kódy', 'Schválenia', 'História', 'Zariadenia', 'Nastavenia']);
      await rig.dispose(tester);
    });

    testWidgets('the lock screen is Slovak, also its button and a failed try', (tester) async {
      slovakDevice(tester);
      final auth = FakeAuthenticator()..outcome = BiometricOutcome.failed;
      final lock = LockController(authenticator: auth, store: MemoryLockStore());
      addTearDown(lock.dispose);
      final rig = ShellRig();
      await rig.pump(tester, lock: lock);
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('lock-screen')), findsOneWidget);
      expect(find.text(LockTexts.sk.text('locked')), findsOneWidget);
      expect(find.text('Odomknúť'), findsOneWidget);
      expect(find.text(LockTexts.sk.text('failed')), findsOneWidget);
      expect(find.text('Unlock'), findsNothing);
      await rig.dispose(tester);
    });
  });
}
