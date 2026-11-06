import 'dart:io';

import 'package:biatec_authenticator/codes/backup_screen.dart';
import 'package:biatec_authenticator/codes/codes_controller.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_screens.dart';
import 'package:biatec_authenticator/totp/backup/backup_service.dart';

import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../totp/backup/backup_service_test.dart'
    show FakeFiles, cheapCodec, pass, serviceOf;
import '../totp/vault/support.dart' show account;
import 'support.dart';

Future<void> pumpScreen(WidgetTester tester, Widget home,
    {double text = 1, bool dark = false}) async {
  tester.view.physicalSize = const Size(400, 3200);
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester.pumpWidget(MaterialApp(
      theme: buildTheme(dark ? Brightness.dark : Brightness.light),
      home: home));
  await tester.pump(const Duration(milliseconds: 50));
}

Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 6; i++) {
    await tester
        .runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
    await tester.pump(const Duration(milliseconds: 20));
  }
}

class BackupScreenParts {
  BackupScreenParts(this.service, this.controller);
  final BackupService service;
  final CodesController controller;
}

void main() {
  late CodesRig rig;
  late FakeFiles files;

  setUp(() async {
    rig = await CodesRig.create();
    files = FakeFiles();
  });

  BackupScreenParts parts() {
    final service = serviceOf(rig, files);
    return BackupScreenParts(service, CodesController(rig.services));
  }

  group('menu', () {
    for (final dark in [false, true]) {
      testWidgets('is readable at 3x text, dark=$dark', (tester) async {
        final p = parts();
        addTearDown(p.controller.dispose);
        await pumpScreen(
            tester, BackupScreen(service: p.service, controller: p.controller),
            text: 3, dark: dark);
        expect(find.byKey(const Key('backup-create')), findsOneWidget);
        expect(find.byKey(const Key('backup-restore')), findsOneWidget);
        expect(tester.takeException(), isNull);
        final handle = tester.ensureSemantics();
        await expectLater(tester, meetsGuideline(textContrastGuideline));
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        handle.dispose();
      });
    }
  });

  group('export screen', () {
    testWidgets('warns that the passphrase cannot be recovered',
        (tester) async {
      final p = parts();
      await pumpScreen(tester, ExportBackupScreen(service: p.service));
      expect(find.byKey(const Key('backup-warning')), findsOneWidget);
      expect(find.textContaining('cannot be recovered'), findsOneWidget);
    });

    testWidgets('the button waits for a good passphrase typed twice',
        (tester) async {
      final p = parts();
      await pumpScreen(tester, ExportBackupScreen(service: p.service));
      FilledButton button() =>
          tester.widget<FilledButton>(find.byKey(const Key('backup-make')));
      expect(button().onPressed, isNull);

      await tester.enterText(
          find.byKey(const Key('passphrase')), 'aaaaaaaaaaaa');
      await tester.pump();
      expect(find.byKey(const Key('passphrase-problem')), findsOneWidget);
      expect(button().onPressed, isNull);

      await tester.enterText(find.byKey(const Key('passphrase')), pass);
      await tester.enterText(
          find.byKey(const Key('passphrase-confirm')), 'other');
      await tester.pump();
      expect(find.byKey(const Key('passphrase-mismatch')), findsOneWidget);
      expect(button().onPressed, isNull);

      await tester.enterText(find.byKey(const Key('passphrase-confirm')), pass);
      await tester.pump();
      expect(button().onPressed, isNotNull);
    });

    testWidgets('the passphrase is hidden until shown', (tester) async {
      final p = parts();
      await pumpScreen(tester, ExportBackupScreen(service: p.service));
      bool obscured() => tester
          .widget<TextField>(find.byKey(const Key('passphrase')))
          .obscureText;
      expect(obscured(), isTrue);
      await tester.tap(find.byKey(const Key('show-passphrase')));
      await tester.pump();
      expect(obscured(), isFalse);
    });

    testWidgets('makes the backup, shares it and clears the fields',
        (tester) async {
      await rig.addAccount(account(issuer: 'A', label: 'a', seed: 1));
      final p = parts();
      await pumpScreen(tester, ExportBackupScreen(service: p.service));
      await tester.enterText(find.byKey(const Key('passphrase')), pass);
      await tester.enterText(find.byKey(const Key('passphrase-confirm')), pass);
      await tester.pump();
      await tester.tap(find.byKey(const Key('backup-make')));
      await settle(tester);
      expect(find.byKey(const Key('backup-done')), findsOneWidget);
      expect(files.shared, hasLength(1));
      expect(
          tester
              .widget<TextField>(find.byKey(const Key('passphrase')))
              .controller!
              .text,
          isEmpty);
    });

    testWidgets('a refused fingerprint says that no backup was made',
        (tester) async {
      await rig.addAccount(account(seed: 1));
      rig.auth.outcome = BiometricOutcome.cancelled;
      final p = parts();
      await pumpScreen(tester, ExportBackupScreen(service: p.service));
      await tester.enterText(find.byKey(const Key('passphrase')), pass);
      await tester.enterText(find.byKey(const Key('passphrase-confirm')), pass);
      await tester.pump();
      await tester.tap(find.byKey(const Key('backup-make')));
      await settle(tester);
      expect(find.byKey(const Key('backup-problem')), findsOneWidget);
      expect(files.shared, isEmpty);
    });

    for (final dark in [false, true]) {
      testWidgets('fits at 3x text with a long warning, dark=$dark',
          (tester) async {
        final p = parts();
        await pumpScreen(tester, ExportBackupScreen(service: p.service),
            text: 3, dark: dark);
        await tester.enterText(find.byKey(const Key('passphrase')), pass);
        await tester.pump();
        expect(tester.takeException(), isNull);
        final handle = tester.ensureSemantics();
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        handle.dispose();
      });
    }
  });

  group('import screen', () {
    testWidgets('previews the accounts and adds them to the list',
        (tester) async {
      await rig.addAccount(account(issuer: 'A', label: 'a', seed: 1));
      files.next = await cheapCodec().encrypt([
        account(issuer: 'A', label: 'a', seed: 1),
        account(issuer: 'B', label: 'b', seed: 2),
      ], pass);
      final p = parts();
      addTearDown(p.controller.dispose);
      await p.controller.load();
      await pumpScreen(tester,
          ImportBackupScreen(service: p.service, controller: p.controller));
      await tester.enterText(find.byKey(const Key('passphrase')), pass);
      await tester.pump();
      await tester.tap(find.byKey(const Key('restore-choose')));
      await settle(tester);
      expect(find.byKey(const Key('restore-item-0')), findsOneWidget);
      expect(find.byKey(const Key('restore-duplicates')), findsOneWidget);
      await tester.tap(find.byKey(const Key('restore-apply')));
      await settle(tester);
      expect(find.byKey(const Key('restore-done')), findsOneWidget);
      expect(p.controller.entries.map((e) => e.info.issuer),
          containsAll(['A', 'B']));
      expect(p.controller.entries, hasLength(2));
    });

    testWidgets('a wrong passphrase gives one generic message', (tester) async {
      files.next = await cheapCodec().encrypt([account(seed: 1)], pass);
      final p = parts();
      addTearDown(p.controller.dispose);
      await pumpScreen(tester,
          ImportBackupScreen(service: p.service, controller: p.controller));
      await tester.enterText(
          find.byKey(const Key('passphrase')), 'not the passphrase at all');
      await tester.pump();
      await tester.tap(find.byKey(const Key('restore-choose')));
      await settle(tester);
      expect(find.byKey(const Key('restore-problem')), findsOneWidget);
      expect(find.textContaining('could not be opened'), findsWidgets);
      expect(find.byKey(const Key('restore-apply')), findsNothing);
    });

    testWidgets('after a biometric change the restore replaces the vault',
        (tester) async {
      await rig.addAccount(account(issuer: 'Old', label: 'o', seed: 1));
      files.next = await cheapCodec()
          .encrypt([account(issuer: 'New', label: 'n', seed: 2)], pass);
      rig.keys.invalidated = true;
      final probe = CodesController(rig.services);
      await probe.load();
      probe.dispose();
      final p = parts();
      await pumpScreen(
          tester,
          Builder(
              builder: (context) => TextButton(
                  onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute<void>(
                          builder: (_) => ImportBackupScreen(
                              service: p.service,
                              afterBiometricsChanged: true))),
                  child: const Text('open'))));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('passphrase')), pass);
      await tester.pump();
      await tester.tap(find.byKey(const Key('restore-choose')));
      await settle(tester);
      await tester.tap(find.byKey(const Key('restore-apply')));
      await settle(tester);
      await tester.pumpAndSettle();
      expect(find.text('open'), findsOneWidget, reason: 'closed itself');
      expect((await rig.vault.list()).map((i) => i.issuer), ['New']);
    });
  });

  group('biometrics changed screen', () {
    testWidgets('offers the restore only when it is wired', (tester) async {
      await pumpScreen(tester, const BiometricsChangedScreen());
      expect(find.byKey(const Key('restore-from-backup')), findsNothing);
      await pumpScreen(tester,
          BiometricsChangedScreen(restoreBuilder: (_) => const SizedBox()),
          text: 3);
      expect(find.byKey(const Key('restore-from-backup')), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('the seed stays inside the vault and the backup', () {
    test('only the vault, the backup and the OTP definitions touch it', () {
      const allowed = {
        'lib/totp/vault/totp_vault.dart',
        'lib/totp/backup/backup_format.dart',
        'lib/totp/backup/backup_service.dart',
        'lib/totp/core/otp_account.dart',
        'lib/totp/core/otp_uri.dart',
        'lib/totp/core/otp_generator.dart',
      };
      final needles = ['OtpUri.write', '.secret', 'copySecret'];
      final offenders = <String>[];
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final path = f.path.replaceAll('\\', '/');
        if (allowed.contains(path)) continue;
        final text = f.readAsStringSync();
        for (final n in needles) {
          if (n == '.secret') {
            // Reading the seed of an account (not a "secret" key purpose or a word).
            if (RegExp(r'\b(account|a|acc|entry\.account)\.secret\b')
                .hasMatch(text)) {
              offenders.add('$path: $n');
            }
          } else if (text.contains(n)) {
            offenders.add('$path: $n');
          }
        }
      }
      expect(offenders, isEmpty);
    });

    test('the backup screens never print or store the passphrase', () {
      final text = File('lib/codes/backup_screen.dart').readAsStringSync();
      expect(text, isNot(contains('print(')));
      expect(text, isNot(contains('debugPrint')));
      expect(text, isNot(contains('SharedPreferences')));
      expect(text, isNot(contains('secure_storage')));
    });
  });
}
