import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/l10n/texts.dart';
import 'package:biatec_authenticator/privacy/privacy_screen.dart';
import 'package:biatec_authenticator/settings/settings_screen.dart';
import 'package:biatec_authenticator/settings/theme_controller.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import 'support.dart';

class _MemoryThemeStore implements ThemePreferenceStore {
  String? value;

  @override
  Future<String?> read() async => value;

  @override
  Future<void> write(String v) async => value = v;
}

/// Pumps [screen] on a phone set to Slovak: the device's language and the app's locale.
Future<void> _openInSlovak(WidgetTester tester, Widget screen,
    {Size size = const Size(360, 2400)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.localeTestValue = const Locale('sk');
  addTearDown(tester.platformDispatcher.clearLocaleTestValue);
  await tester.pumpWidget(MaterialApp(
      locale: const Locale('sk'),
      supportedLocales: appLocales,
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      theme: buildTheme(Brightness.light),
      home: ThemeScope(
          controller: ThemeController(store: _MemoryThemeStore()),
          child: screen)));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('on a phone set to Slovak the Settings tab is Slovak', (tester) async {
    final rig = SettingsRig();
    await _openInSlovak(tester,
        SettingsScreen(services: rig.services, config: AppConfig.resolve('')));
    expect(find.text('Nastavenia'), findsOneWidget);
    for (final title in ['Zabezpečenie', 'Vzhľad', 'O aplikácii']) {
      expect(find.text(title), findsOneWidget, reason: title);
    }
    for (final english in ['Settings', 'Security', 'Appearance', 'About', 'Biometric check', 'Version']) {
      expect(find.text(english), findsNothing, reason: english);
    }
    expect(find.text('Biometrické overenie'), findsOneWidget);
    expect(find.text('Limit na schvaľovanie'), findsOneWidget);
    expect(find.text('Systém'), findsOneWidget);
    expect(find.text('Verzia'), findsOneWidget);
    expect(find.text('Súkromie'), findsOneWidget);
  });

  testWidgets('and so is the Privacy screen', (tester) async {
    await _openInSlovak(tester, const PrivacyScreen(),
        size: const Size(360, 6000));
    expect(find.text('Súkromie'), findsOneWidget);
    expect(find.text('Uložené v tomto telefóne'), findsOneWidget);
    expect(find.text('Odosielané do Biatec OIDC'), findsOneWidget);
    expect(find.text('Nikdy sa nezbiera'), findsOneWidget);
    expect(find.text('Stored on this phone'), findsNothing);
  });
}
