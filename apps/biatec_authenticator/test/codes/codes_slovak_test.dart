import 'package:biatec_authenticator/codes/codes_screen.dart';
import 'package:biatec_authenticator/l10n/texts.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import 'support.dart';

const key = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';
const link =
    'otpauth://totp/GitHub:alice%40example.com?secret=$key&issuer=GitHub';

/// The Codes tab on a phone set to Slovak (T-251).
Future<CodesRig> openSlovak(WidgetTester tester) async {
  final rig = await CodesRig.create(now: DateTime.utc(2026, 11, 2, 10));
  tester.view.physicalSize = const Size(400, 1200);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.localeTestValue = const Locale('sk');
  addTearDown(tester.platformDispatcher.clearLocaleTestValue);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    locale: const Locale('sk'),
    supportedLocales: appLocales,
    localizationsDelegates: GlobalMaterialLocalizations.delegates,
    home: CodesScreen(services: rig.services),
  ));
  for (var i = 0; i < 4; i++) {
    await tester.pump(const Duration(milliseconds: 20));
  }
  return rig;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('the empty Codes tab is in Slovak', (tester) async {
    await openSlovak(tester);
    expect(find.text('Kódy'), findsOneWidget);
    expect(find.text('Zatiaľ žiadne kódy'), findsOneWidget);
    expect(find.text('Naskenovať QR kód'), findsOneWidget);
    expect(find.text('Vložiť odkaz'), findsOneWidget);
    expect(find.text('Zadať kľúč na nastavenie'), findsOneWidget);
    expect(find.byTooltip('Hľadať'), findsOneWidget);
    expect(find.byTooltip('Záloha a obnova'), findsOneWidget);
    expect(find.text('Codes'), findsNothing);
    expect(find.text('Scan a QR code'), findsNothing);
  });

  testWidgets(
      'adding an account by link is in Slovak, also the refusal of the parser',
      (tester) async {
    final rig = await openSlovak(tester);
    await tester.tap(find.byKey(const Key('empty-link')));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(AppBar, 'Vložiť odkaz'), findsOneWidget);
    expect(find.text('Pokračovať'), findsOneWidget);
    expect(find.text('Vložte celý odkaz.'), findsOneWidget);

    await tester.enterText(
        find.byKey(const Key('link-field')), 'https://example.com');
    await tester.tap(find.byKey(const Key('link-continue')));
    await tester.pumpAndSettle();
    expect(find.text('Účet sa nepridal'), findsOneWidget);
    expect(find.text('Toto nie je odkaz na nastavenie kódu.'), findsOneWidget);

    await tester.enterText(find.byKey(const Key('link-field')), link);
    await tester.tap(find.byKey(const Key('link-continue')));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(AppBar, 'Skontrolujte účet'), findsOneWidget);
    expect(find.text('Časový, nový kód každých 30 sekúnd'), findsOneWidget);
    expect(find.text('6 číslic, SHA1'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Pridať účet'), findsOneWidget);

    await tester.tap(find.byKey(const Key('save-account')));
    await tester.pumpAndSettle();
    expect(await rig.vault.list(), hasLength(1));
    expect(find.text('Kódy'), findsOneWidget);
    expect(find.widgetWithText(FloatingActionButton, 'Pridať účet'),
        findsOneWidget);
    expect(find.byTooltip('Ďalšie možnosti pre GitHub, alice@example.com'),
        findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the backup screen is in Slovak', (tester) async {
    await openSlovak(tester);
    await tester.tap(find.byKey(const Key('backup')));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(AppBar, 'Záloha'), findsOneWidget);
    expect(find.text('Vytvoriť zálohu'), findsOneWidget);
    expect(find.text('Obnoviť zo zálohy'), findsOneWidget);

    await tester.tap(find.byKey(const Key('backup-create')));
    await tester.pumpAndSettle();
    expect(find.text('Prístupová fráza sa nedá obnoviť'), findsOneWidget);
    await tester.enterText(find.byKey(const Key('passphrase')), 'short');
    await tester.pump();
    expect(find.text('Sila: Príliš slabá'), findsOneWidget);
    expect(find.textContaining('Použite najmenej 12 znakov.'), findsOneWidget);
    expect(find.text('Vytvoriť zálohu'), findsWidgets);
  });
}
