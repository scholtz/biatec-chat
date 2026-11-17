import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_texts.dart';
import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/pending_screen.dart';
import 'package:biatec_authenticator/l10n/texts.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';

import 'approval_test.dart' show Harness, vectorRequest;

/// A device set to Slovak (T-251): the app's texts and Flutter's own follow it.
Future<void> _pumpSlovak(WidgetTester tester, Widget home) async {
  tester.view.physicalSize = const Size(400, 2000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.localeTestValue = const Locale('sk');
  addTearDown(tester.platformDispatcher.clearLocaleTestValue);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(Brightness.light),
    locale: const Locale('sk'),
    supportedLocales: appLocales,
    localizationsDelegates: GlobalMaterialLocalizations.delegates,
    home: home,
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _end(WidgetTester tester) => tester.pumpWidget(const SizedBox());

void main() {
  testWidgets('the pending list is Slovak on a Slovak device', (tester) async {
    final h = Harness(vectorRequest('stepup-transaction-approve'));
    await _pumpSlovak(
        tester,
        PendingScreen(
            services: h.services, pollEvery: const Duration(hours: 1)));
    expect(find.text('Čakajúce schválenia'), findsOneWidget);
    expect(find.byTooltip('Obnoviť'), findsOneWidget);
    // The time left on the card.
    expect(find.text('Zostáva 4 min'), findsOneWidget);
    expect(find.text('Pending approvals'), findsNothing);
    await _end(tester);
  });

  testWidgets(
      'the approval screen, its buttons and a message of the controller are Slovak',
      (tester) async {
    final h = Harness(vectorRequest('stepup-transaction-approve'));
    await _pumpSlovak(
        tester,
        ApprovalScreen(
            services: h.services,
            authReqId: h.oidc.request['auth_req_id'] as String));
    expect(find.text('Schválenie'), findsOneWidget);
    expect(find.text('Schváliť'), findsOneWidget);
    expect(find.text('Zamietnuť'), findsOneWidget);
    expect(find.text('Schvaľujete'), findsOneWidget);
    expect(find.text('Kód zobrazený tam, kde ste túto akciu spustili'),
        findsOneWidget);
    expect(find.textContaining('Vyprší o '), findsOneWidget);
    expect(find.text('Approve'), findsNothing);

    // A code that is not six digits: the banner's title follows the screen. The controller's message follows the device's language
    // (`appLanguageIsSlovak()` reads `PlatformDispatcher.instance`, which `localeTestValue` does not change), and is recognised by the screen in either language.
    await tester.enterText(find.byKey(const Key('binding-code')), '12');
    await tester.tap(find.byKey(const Key('approve')));
    await tester.pump();
    expect(find.text('Nepodarilo sa dokončiť'), findsOneWidget);
    expect(find.textContaining(ApprovalTexts.of().text('enterCode')),
        findsOneWidget);
    expect(h.oidc.receipts, isEmpty);
    await _end(tester);
  });

  testWidgets('the history writes its days and statuses in Slovak',
      (tester) async {
    final store = MemoryHistoryStore();
    await store.add(HistoryEntry(
      authReqId: 'older',
      decision: 'deny',
      summary: 'Platba: 10.00 EUR',
      clientLabel: 'bank.example',
      decidedAt: DateTime(2026, 10, 22, 9, 5),
      receipt: const {'version': '1', 'alg': 'ES256'},
    ));
    await store.add(HistoryEntry(
      authReqId: 'today',
      decision: 'approve',
      summary: 'Platba: 5.00 EUR',
      clientLabel: 'bank.example',
      decidedAt: DateTime(2026, 10, 24, 9, 5),
      receipt: const {'version': '1', 'alg': 'ES256'},
    ));
    await _pumpSlovak(tester,
        HistoryScreen(history: store, clock: () => DateTime(2026, 10, 24, 15)));
    expect(find.text('História schválení'), findsOneWidget);
    expect(find.text('Dnes'), findsOneWidget);
    // The date of an older day comes from Flutter's Slovak texts.
    expect(find.text('štvrtok 22. októbra 2026'), findsOneWidget);
    expect(find.text('Všetko'), findsOneWidget);
    expect(find.text('Schválené'), findsWidgets);
    expect(find.text('Zamietnuté'), findsWidgets);
    expect(find.text('Today'), findsNothing);
    await _end(tester);
  });
}
