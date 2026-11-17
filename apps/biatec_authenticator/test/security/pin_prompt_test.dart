import 'package:biatec_authenticator/security/pin_prompt.dart';
import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  final navigator = GlobalKey<NavigatorState>();

  Future<void> pumpApp(WidgetTester tester) => tester.pumpWidget(MaterialApp(
        navigatorKey: navigator,
        home: const Scaffold(body: Text('app')),
      ));

  Future<void> type(WidgetTester tester, String key, String text) async {
    await tester.enterText(find.byKey(Key(key)), text);
    await tester.pump();
  }

  testWidgets('a new PIN: typed twice, short and different ones are refused in the dialog, a good one is returned', (tester) async {
    await pumpApp(tester);
    final answer = showPinDialog(navigator.currentState!, create: true, reason: '');
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget);
    expect(find.byKey(const Key('pin-warning')), findsOneWidget, reason: 'the user is told the PIN cannot be recovered');
    expect(find.byKey(const Key('pin-repeat')), findsOneWidget);

    await type(tester, 'pin-field', '12345');
    await type(tester, 'pin-repeat', '12345');
    await tester.tap(find.byKey(const Key('pin-ok')));
    await tester.pump();
    expect(find.text(PinStrings.en.text('tooShort')), findsOneWidget);

    await type(tester, 'pin-field', '123456');
    expect(find.byKey(const Key('pin-error')), findsNothing, reason: 'typing clears the message');
    await type(tester, 'pin-repeat', '123457');
    await tester.tap(find.byKey(const Key('pin-ok')));
    await tester.pump();
    expect(find.text(PinStrings.en.text('different')), findsOneWidget);
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget, reason: 'still open');

    await type(tester, 'pin-repeat', '123456');
    await tester.tap(find.byKey(const Key('pin-ok')));
    await tester.pumpAndSettle();
    expect(await answer, '123456');
    expect(find.byKey(const Key('pin-dialog')), findsNothing);
  });

  testWidgets('the existing PIN: one hidden field with the reason, Cancel answers null, an empty field does nothing', (tester) async {
    await pumpApp(tester);
    var answer = showPinDialog(navigator.currentState!, create: false, reason: 'Approve EUR 5 to Bob');
    await tester.pumpAndSettle();
    expect(find.text('Approve EUR 5 to Bob'), findsOneWidget);
    expect(find.byKey(const Key('pin-repeat')), findsNothing);
    expect(tester.widget<TextField>(find.byKey(const Key('pin-field'))).obscureText, isTrue);
    await tester.tap(find.byKey(const Key('pin-ok')));
    await tester.pump();
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget, reason: 'an empty PIN is not sent');
    await tester.tap(find.byKey(const Key('pin-cancel')));
    await tester.pumpAndSettle();
    expect(await answer, isNull);

    // Any PIN is passed on: the TPM, not the dialog, says whether it is the PIN.
    answer = showPinDialog(navigator.currentState!, create: false, reason: 'Unlock');
    await tester.pumpAndSettle();
    await type(tester, 'pin-field', 'abc');
    await tester.tap(find.byKey(const Key('pin-show')));
    await tester.pump();
    expect(tester.widget<TextField>(find.byKey(const Key('pin-field'))).obscureText, isFalse);
    await tester.tap(find.byKey(const Key('pin-ok')));
    await tester.pumpAndSettle();
    expect(await answer, 'abc');
  });

  testWidgets('one dialog at a time, it cannot be dismissed by a tap outside, and the existing-PIN dialog closes itself', (tester) async {
    await pumpApp(tester);
    final first = showPinDialog(navigator.currentState!, create: false, reason: 'Unlock');
    await tester.pumpAndSettle();
    expect(await showPinDialog(navigator.currentState!, create: false, reason: 'Second'), isNull);
    expect(find.text('Second'), findsNothing);
    await tester.tapAt(const Offset(5, 5));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget);
    await tester.pump(pinEnterTimeout + const Duration(seconds: 1));
    await tester.pumpAndSettle();
    expect(await first, isNull);
    expect(find.byKey(const Key('pin-dialog')), findsNothing);
    // And another one can open after it.
    final next = showPinDialog(navigator.currentState!, create: true, reason: '');
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget);
    await tester.pump(pinEnterTimeout * 3);
    expect(find.byKey(const Key('pin-dialog')), findsOneWidget, reason: 'a new PIN has no time limit');
    await tester.tap(find.byKey(const Key('pin-cancel')));
    await tester.pumpAndSettle();
    expect(await next, isNull);
  });

  testWidgets('the Slovak texts are shown with their diacritics', (tester) async {
    await tester.pumpWidget(const MaterialApp(home: Scaffold(body: PinDialog(create: true, reason: '', strings: PinStrings.sk))));
    expect(find.text('Zvoľte si PIN pre túto aplikáciu'), findsOneWidget);
    expect(find.text('Zrušiť'), findsOneWidget);
  });

  test('both languages have every text, and askPin never throws', () async {
    expect(PinStrings.sk.keys.toSet(), PinStrings.en.keys.toSet());
    for (final key in PinStrings.en.keys) {
      expect(PinStrings.sk.text(key), isNotEmpty);
    }
    pinPromptHandler = null;
    expect(await askPin(create: false), isNull);
    pinPromptHandler = ({required create, required reason}) async => throw StateError('no screen');
    expect(await askPin(create: true), isNull);
    pinPromptHandler = null;
  });
}
