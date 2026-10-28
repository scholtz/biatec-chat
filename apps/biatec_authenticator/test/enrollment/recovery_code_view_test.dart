import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets(
      'the recovery code is shown as plain text, not selectable (BCHAT-031)',
      (tester) async {
    var saved = false;
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(
        body: RecoveryCodeView(
            code: 'ABCD-EFGH-IJKL', onSaved: () => saved = true),
      ),
    ));
    expect(find.text('ABCD-EFGH-IJKL'), findsOneWidget);
    expect(find.byType(SelectableText), findsNothing);
    await tester.tap(find.text('I have saved it'));
    expect(saved, isTrue);
  });
}
