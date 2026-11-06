import 'dart:typed_data';

import 'package:biatec_authenticator/codes/codes_controller.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/totp/backup/argon2.dart';
import 'package:biatec_authenticator/totp/backup/backup_files.dart';
import 'package:biatec_authenticator/totp/backup/backup_format.dart';
import 'package:biatec_authenticator/totp/backup/backup_service.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../codes/support.dart';
import '../vault/support.dart' show account;

const pass = 'correct horse battery staple';

Future<Uint8List> inline(
        Uint8List password, Uint8List salt, Argon2idParams p) async =>
    argon2id(password, salt, p);

class FakeFiles implements BackupFiles {
  final List<(Uint8List, String)> shared = [];
  Uint8List? next;
  int picks = 0;
  bool offered = true;

  @override
  Future<bool> share(Uint8List bytes, String fileName) async {
    shared.add((Uint8List.fromList(bytes), fileName));
    return offered;
  }

  @override
  Future<Uint8List?> pick() async {
    picks++;
    return next;
  }
}

BackupCodec cheapCodec() => BackupCodec(
    policy: const BackupPolicy(
        floor: Argon2idParams(memoryKiB: 64, iterations: 1, lanes: 1)),
    kdf: inline);

BackupService serviceOf(CodesRig rig, FakeFiles files) => BackupService(
    lock: rig.lock,
    vault: rig.vault,
    files: files,
    codec: cheapCodec(),
    clock: () => DateTime.utc(2026, 11, 6));

Future<void> loseBiometrics(CodesRig rig) async {
  rig.keys.invalidated = true;
  final controller = CodesController(rig.services);
  await controller.load();
  controller.dispose();
}

void main() {
  group('export', () {
    test('asks for a fresh biometric, then shares one file of every account',
        () async {
      final rig = await CodesRig.create();
      final files = FakeFiles();
      await rig.addAccount(account(issuer: 'A', label: 'a', seed: 1));
      await rig.addAccount(account(issuer: 'B', label: 'b', seed: 2));
      final before = rig.auth.prompts;
      final n = await serviceOf(rig, files).export(pass, pass);
      expect(n, 2);
      expect(rig.auth.prompts, before + 1);
      expect(rig.auth.reasons.last, contains('backup'));
      expect(files.shared.single.$2,
          'biatec-authenticator-backup-20261106.biatecbackup');
    });

    test('a refused biometric reads nothing and shares nothing', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles();
      await rig.addAccount(account(seed: 1));
      final reads = rig.keys.openedPlain.length;
      rig.auth.outcome = BiometricOutcome.cancelled;
      await expectLater(serviceOf(rig, files).export(pass, pass),
          throwsA(isA<BackupRefused>()));
      expect(files.shared, isEmpty);
      expect(rig.keys.openedPlain.length, reads);
    });

    test('a weak or unconfirmed passphrase is refused before any prompt',
        () async {
      final rig = await CodesRig.create();
      final files = FakeFiles();
      await rig.addAccount(account(seed: 1));
      final before = rig.auth.prompts;
      final s = serviceOf(rig, files);
      await expectLater(
          s.export('short', 'short'), throwsA(isA<BackupRefused>()));
      await expectLater(
          s.export(pass, '$pass!'), throwsA(isA<BackupRefused>()));
      expect(rig.auth.prompts, before);
      expect(files.shared, isEmpty);
    });

    test('a closed share sheet is not a finished backup', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles()..offered = false;
      await rig.addAccount(account(seed: 1));
      await expectLater(serviceOf(rig, files).export(pass, pass),
          throwsA(isA<BackupRefused>()));
    });

    test('no accounts: nothing to back up', () async {
      final rig = await CodesRig.create();
      await expectLater(serviceOf(rig, FakeFiles()).export(pass, pass),
          throwsA(isA<BackupRefused>()));
    });

    test('the exported bytes hold no name in the clear', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles();
      await rig.addAccount(account(issuer: 'Visible', label: 'who', seed: 5));
      await serviceOf(rig, files).export(pass, pass);
      final text = String.fromCharCodes(files.shared.single.$1);
      expect(text, isNot(contains('Visible')));
      expect(text, isNot(contains('who')));
    });
  });

  group('import', () {
    Future<Uint8List> makeFile(List<OtpAccount> accounts) =>
        cheapCodec().encrypt(accounts, pass);

    test('reports what is new and what is a duplicate', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles()
        ..next = await makeFile([
          account(issuer: 'A', label: 'a', seed: 1),
          account(issuer: 'B', label: 'b', seed: 2),
        ]);
      final existing = [account(issuer: 'A', label: 'a', seed: 1)];
      final preview =
          await serviceOf(rig, files).open(pass, existing: existing);
      expect(preview!.toAdd.map((a) => a.issuer), ['B']);
      expect(preview.duplicates, 1);
      preview.wipe();
      expect(preview.toAdd.single.isWiped, isTrue);
    });

    test('a closed picker is not an error', () async {
      final rig = await CodesRig.create();
      expect(await serviceOf(rig, FakeFiles()).open(pass), isNull);
    });

    test('a refused biometric never opens the picker', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles()..next = await makeFile([account(seed: 1)]);
      rig.auth.outcome = BiometricOutcome.cancelled;
      await expectLater(
          serviceOf(rig, files).open(pass), throwsA(isA<BackupRefused>()));
      expect(files.picks, 0);
    });

    test('a wrong passphrase gives the one generic failure', () async {
      final rig = await CodesRig.create();
      final files = FakeFiles()..next = await makeFile([account(seed: 1)]);
      await expectLater(serviceOf(rig, files).open('not the passphrase at all'),
          throwsA(isA<BackupFailure>()));
    });

    test('restoring on another phone reproduces the accounts', () async {
      final source = await CodesRig.create();
      final files = FakeFiles();
      await source.addAccount(account(issuer: 'A', label: 'a', seed: 1));
      await source.addAccount(account(
          issuer: 'B', label: 'b', seed: 2, type: OtpType.hotp, counter: 9));
      await serviceOf(source, files).export(pass, pass);

      final target = await CodesRig.create();
      files.next = files.shared.single.$1;
      final preview = (await serviceOf(target, files).open(pass))!;
      expect(preview.toAdd, hasLength(2));
      for (final a in preview.toAdd) {
        await target.vault.add(a);
      }
      final infos = await target.vault.list();
      expect(infos.map((i) => i.issuer), containsAll(['A', 'B']));
      for (final i in infos) {
        final got = await target.vault.readSecret(i.id);
        final want = account(
            issuer: i.issuer,
            label: i.label,
            seed: i.issuer == 'A' ? 1 : 2,
            type: i.issuer == 'A' ? OtpType.totp : OtpType.hotp,
            counter: i.issuer == 'A' ? 0 : 9);
        expect(got.secret, want.secret);
        expect(got.counter, want.counter);
      }
    });
  });

  group('after the biometrics changed', () {
    test('the old vault is replaced and the app returns to the lock', () async {
      final rig = await CodesRig.create();
      await rig.addAccount(account(issuer: 'Old', label: 'o', seed: 1));
      final files = FakeFiles()
        ..next = await cheapCodec()
            .encrypt([account(issuer: 'New', label: 'n', seed: 2)], pass);
      await loseBiometrics(rig);
      expect(rig.lock.state, AppLockState.biometricsChanged);

      final s = serviceOf(rig, files);
      final preview = (await s.openAfterBiometricsChanged(pass))!;
      expect(rig.auth.reasons.last, contains('Restore'));
      expect(await s.restoreIntoFreshVault(preview), 1);
      expect(rig.lock.state, isNot(AppLockState.biometricsChanged));
      final infos = await rig.vault.list();
      expect(infos.map((i) => i.issuer), ['New']);
    });

    test('a refused biometric leaves everything as it was', () async {
      final rig = await CodesRig.create();
      await rig.addAccount(account(seed: 1));
      await loseBiometrics(rig);
      final files = FakeFiles()
        ..next = await cheapCodec().encrypt([account(seed: 2)], pass);
      rig.auth.outcome = BiometricOutcome.cancelled;
      await expectLater(serviceOf(rig, files).openAfterBiometricsChanged(pass),
          throwsA(isA<BackupRefused>()));
      expect(files.picks, 0);
      expect(rig.lock.state, AppLockState.biometricsChanged);
    });

    test('a failure part-way can be retried from the same preview', () async {
      final rig = await CodesRig.create();
      await rig.addAccount(account(issuer: 'Old', label: 'o', seed: 1));
      final files = FakeFiles()
        ..next = await cheapCodec().encrypt([
          account(issuer: 'N1', label: 'a', seed: 2),
          account(issuer: 'N2', label: 'b', seed: 3),
        ], pass);
      await loseBiometrics(rig);
      final s = serviceOf(rig, files);
      final preview = (await s.openAfterBiometricsChanged(pass))!;
      rig.storage.failWrites = true;
      await expectLater(s.restoreIntoFreshVault(preview), throwsA(anything));
      expect(preview.toAdd.any((a) => a.isWiped), isFalse,
          reason: 'still there to retry');
      rig.storage.failWrites = false;
      expect(await s.restoreIntoFreshVault(preview), 2);
      expect((await rig.vault.list()).map((i) => i.issuer),
          unorderedEquals(['N1', 'N2']));
    });

    test('an empty backup never replaces the vault', () async {
      final rig = await CodesRig.create();
      await rig.addAccount(account(seed: 1));
      await loseBiometrics(rig);
      final s = serviceOf(rig, FakeFiles());
      await expectLater(s.restoreIntoFreshVault(ImportPreview([], 0)),
          throwsA(isA<BackupRefused>()));
      expect(rig.lock.state, AppLockState.biometricsChanged);
    });

    test('it cannot be used while the app is simply unlocked', () async {
      final rig = await CodesRig.create();
      expect(await rig.lock.verifyForRestore(), isFalse);
    });
  });
}
