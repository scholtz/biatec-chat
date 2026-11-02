import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../shell/app_shell_test.dart' show ShellRig;

/// Every top-level screen of the shell, at normal and 3x text, in light and dark: nothing overflows, every control is at least 48 dp, and every control has a label.
void main() {
  for (final tab in AppTab.values) {
    for (final dark in [false, true]) {
      for (final text in [1.0, 3.0]) {
        testWidgets(
            '${tab.label}: ${dark ? 'dark' : 'light'}, text x${text.toInt()}',
            (tester) async {
          final semantics = tester.ensureSemantics();
          tester.platformDispatcher.platformBrightnessTestValue =
              dark ? Brightness.dark : Brightness.light;
          tester.platformDispatcher.textScaleFactorTestValue = text;
          addTearDown(tester.platformDispatcher.clearAllTestValues);
          final rig = ShellRig();
          await rig.pump(tester, size: const Size(320, 568));
          rig.shell.select(tab);
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull,
              reason: 'no overflow on ${tab.label}');
          await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
          await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
          semantics.dispose();
          await rig.dispose(tester);
        });
      }
    }
  }
}
