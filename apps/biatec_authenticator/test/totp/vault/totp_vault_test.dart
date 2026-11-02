import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/vault/totp_vault.dart';
import 'package:biatec_authenticator/totp/vault/vault_key_store.dart';
import 'package:biatec_authenticator/totp/vault/vault_storage.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

class Rig {
  Rig() {
    vault = TotpVault(keys: keys, storage: storage, versions: versions);
  }
  final keys = FakeVaultKeyStore();
  final storage = MemoryVaultStorage();
  final versions = MemoryVersionStore();
  late final TotpVault vault;

  Map<String, Object?> get file =>
      (jsonDecode(utf8.decode(storage.bytes!)) as Map).cast<String, Object?>();

  void putFile(Map<String, Object?> f) =>
      storage.bytes = Uint8List.fromList(utf8.encode(jsonEncode(f)));
}

bool hasBytes(Uint8List haystack, List<int> needle) {
  if (needle.isEmpty) return true;
  for (var i = 0; i + needle.length <= haystack.length; i++) {
    var ok = true;
    for (var j = 0; j < needle.length; j++) {
      if (haystack[i + j] != needle[j]) {
        ok = false;
        break;
      }
    }
    if (ok) return true;
  }
  return false;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  group('round trip', () {
    test('an account added and read back is identical', () async {
      final r = Rig();
      final a = account(counter: 0);
      final info = await r.vault.add(a);
      final back = await r.vault.readSecret(info.id);
      expect(back.issuer, 'GitHub');
      expect(back.label, 'alice@example.com');
      expect(back.copySecret(), a.copySecret());
      expect(back.algorithm, a.algorithm);
      expect(back.digits, a.digits);
      expect(back.period, a.period);
      expect(back.id, info.id);
      back.wipe();
    });

    test('neither the secret nor the names appear in the file', () async {
      final dir = await Directory.systemTemp.createTemp('vault');
      addTearDown(() => dir.delete(recursive: true));
      final file = File('${dir.path}/vault.bin');
      final keys = FakeVaultKeyStore();
      final vault = TotpVault(
          keys: keys,
          storage: FileVaultStorage(file),
          versions: MemoryVersionStore());
      final a =
          account(issuer: 'DistinctiveIssuer', label: 'secret.person@x.org');
      final secret = a.copySecret();
      await vault.add(a);
      final bytes = await file.readAsBytes();
      expect(hasBytes(bytes, secret), isFalse, reason: 'raw secret');
      expect(hasBytes(bytes, utf8.encode(base64.encode(secret))), isFalse);
      expect(
          hasBytes(
              bytes,
              utf8.encode(secret
                  .map((b) => b.toRadixString(16).padLeft(2, '0'))
                  .join())),
          isFalse);
      expect(hasBytes(bytes, utf8.encode('DistinctiveIssuer')), isFalse);
      expect(hasBytes(bytes, utf8.encode('secret.person')), isFalse);
      expect(File('${file.path}.tmp').existsSync(), isFalse,
          reason: 'the temporary file is renamed away');
    });

    test('list, update, reorder, delete and counter', () async {
      final r = Rig();
      final a = await r.vault.add(account(issuer: 'A', seed: 1));
      final b = await r.vault
          .add(account(issuer: 'B', seed: 2, type: OtpType.hotp, counter: 4));
      final c = await r.vault.add(account(issuer: 'C', seed: 3));
      expect((await r.vault.list()).map((i) => i.issuer), ['A', 'B', 'C']);

      await r.vault.reorder([c.id, a.id, b.id]);
      expect((await r.vault.list()).map((i) => i.issuer), ['C', 'A', 'B']);
      await expectLater(r.vault.reorder([a.id]), throwsArgumentError);
      await expectLater(
          r.vault.reorder([a.id, a.id, b.id]), throwsArgumentError);

      final u = await r.vault.update(a.id, label: 'new name');
      expect(u.label, 'new name');
      await expectLater(r.vault.update(a.id, label: ' '),
          throwsA(isA<OtpAccountException>()));
      await expectLater(
          r.vault.update(a.id, label: 'ev${String.fromCharCode(0x202E)}il'),
          throwsA(isA<OtpAccountException>()));

      expect(await r.vault.incrementCounter(b.id), 5);
      expect((await r.vault.readSecret(b.id)).counter, 5);
      await expectLater(r.vault.incrementCounter(a.id), throwsArgumentError);

      await r.vault.delete(c.id);
      expect((await r.vault.list()).map((i) => i.issuer), ['A', 'B']);
      await expectLater(r.vault.readSecret(c.id), throwsArgumentError);
    });

    test('operations started together are all kept', () async {
      final r = Rig();
      await Future.wait([
        for (var i = 1; i <= 8; i++)
          r.vault.add(account(issuer: 'S$i', seed: i)),
      ]);
      expect((await r.vault.list()).length, 8);
    });
  });

  group('tampering is detected, never decrypted', () {
    Future<Rig> two() async {
      final r = Rig();
      await r.vault.add(account(issuer: 'A', seed: 1));
      await r.vault.add(account(issuer: 'B', seed: 2));
      return r;
    }

    test('a changed byte in the index', () async {
      final r = await two();
      final f = r.file;
      final index = base64.decode(f['index'] as String);
      index[20] ^= 1;
      f['index'] = base64.encode(index);
      r.putFile(f);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('a changed byte in a record', () async {
      final r = await two();
      final f = r.file;
      final records = (f['records'] as Map).cast<String, Object?>();
      final id = records.keys.first;
      final rec = base64.decode(records[id] as String);
      rec[14] ^= 1;
      records[id] = base64.encode(rec);
      r.putFile(f);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
      expect(r.keys.openedPlain, isEmpty);
    });

    test('two accounts\' ciphertexts swapped', () async {
      final r = await two();
      final f = r.file;
      final records = (f['records'] as Map).cast<String, Object?>();
      final ids = records.keys.toList();
      final x = records[ids[0]];
      records[ids[0]] = records[ids[1]];
      records[ids[1]] = x;
      r.putFile(f);
      await expectLater(
          r.vault.readSecret(ids[0]), throwsA(isA<VaultCorrupt>()));
      expect(r.keys.openedPlain, isEmpty);
    });

    test('a record added from outside', () async {
      final r = await two();
      final f = r.file;
      final records = (f['records'] as Map).cast<String, Object?>();
      records['ff' * 16] = records.values.first;
      r.putFile(f);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('a record removed', () async {
      final r = await two();
      final f = r.file;
      final records = (f['records'] as Map).cast<String, Object?>();
      records.remove(records.keys.first);
      r.putFile(f);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('a truncated file', () async {
      final r = await two();
      r.storage.bytes =
          r.storage.bytes!.sublist(0, r.storage.bytes!.length ~/ 2);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('garbage and an empty file', () async {
      final r = await two();
      r.storage.bytes = Uint8List(0);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
      r.storage.bytes = Uint8List.fromList(List.filled(64, 7));
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('an older version of the file put back', () async {
      final r = Rig();
      await r.vault.add(account(issuer: 'A', seed: 1));
      final old = Uint8List.fromList(r.storage.bytes!);
      await r.vault.add(account(issuer: 'B', seed: 2));
      r.storage.bytes = old;
      await expectLater(r.vault.list(), throwsA(isA<VaultRolledBack>()));
      await expectLater(
          r.vault.readSecret('x'), throwsA(isA<VaultRolledBack>()));
    });

    test('the file deleted after it was written', () async {
      final r = await two();
      r.storage.bytes = null;
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('the version number changed inside the file', () async {
      final r = await two();
      final f = r.file;
      f['version'] = (f['version'] as int) + 5;
      r.putFile(f);
      await expectLater(r.vault.list(), throwsA(isA<VaultCorrupt>()));
    });

    test('a first start has an empty vault', () async {
      final r = Rig();
      expect(await r.vault.list(), isEmpty);
    });

    test('a crash between the file and the counter is not a rollback',
        () async {
      final r = Rig();
      await r.vault.add(account(seed: 1));
      r.versions.version -= 1; // the file is one ahead of the counter
      expect(await r.vault.list(), hasLength(1));
    });
  });

  group('encryption rules', () {
    test('nonces are never reused', () async {
      final r = Rig();
      for (var i = 1; i <= 60; i++) {
        await r.vault.add(account(issuer: 'N$i', seed: i));
      }
      await r.vault
          .reorder((await r.vault.list()).reversed.map((i) => i.id).toList());
      expect(r.keys.nonces.length, greaterThan(120));
      expect(r.keys.nonces.toSet().length, r.keys.nonces.length);
    });

    test('a record is bound to its account id', () async {
      final r = Rig();
      final a = await r.vault.add(account(seed: 1));
      await r.vault.readSecret(a.id);
      expect(r.keys.openedAad.where((s) => s.contains('secret')).single,
          endsWith(a.id));
      expect(
          r.keys.openedAad.any((s) => s.startsWith('biatec.vault.index.v1|')),
          isTrue);
    });

    test('the same secret is sealed differently each time', () async {
      final r = Rig();
      await r.vault.add(account(seed: 1));
      await r.vault.add(account(seed: 1));
      final records = (r.file['records'] as Map).values.toSet();
      expect(records.length, 2);
    });
  });

  group('biometrics', () {
    test('listing needs none; adding and reading a secret each need one',
        () async {
      final r = Rig();
      final a = await r.vault.add(account());
      expect(r.keys.prompts, 1);
      await r.vault.list();
      await r.vault.update(a.id, label: 'x');
      await r.vault.reorder([a.id]);
      expect(r.keys.prompts, 1, reason: 'metadata work asks nothing');
      (await r.vault.readSecret(a.id)).wipe();
      expect(r.keys.prompts, 2);
    });

    test('a refused prompt changes nothing', () async {
      final r = Rig();
      await r.vault.add(account(seed: 1));
      final before = Uint8List.fromList(r.storage.bytes!);
      r.keys.denyAuth = true;
      await expectLater(
          r.vault.add(account(seed: 2)), throwsA(isA<VaultAuthFailed>()));
      expect(r.storage.bytes, before);
      final id = (await r.vault.list()).single.id;
      await expectLater(
          r.vault.readSecret(id), throwsA(isA<VaultAuthFailed>()));
    });

    test('a biometric change is reported on every read and the file is kept',
        () async {
      final r = Rig();
      final a = await r.vault.add(account(seed: 1));
      final b = await r.vault.add(account(seed: 2));
      final before = Uint8List.fromList(r.storage.bytes!);
      r.keys.invalidated = true;
      for (final id in [a.id, b.id]) {
        await expectLater(
            r.vault.readSecret(id), throwsA(isA<VaultBiometricsChanged>()));
      }
      expect(r.storage.bytes, before, reason: 'left intact');
      expect(await r.vault.list(), hasLength(2),
          reason: 'names still listed without a biometric');
      // A later restore (T-226) makes the same file readable again.
      r.keys.invalidated = false;
      (await r.vault.readSecret(a.id)).wipe();
    });
  });

  group('a prompt that never answers', () {
    test('does not hold the queue for good', () async {
      final r = Rig();
      final slow = TotpVault(
        keys: _Hanging(r.keys),
        storage: r.storage,
        versions: r.versions,
        promptTimeout: const Duration(milliseconds: 50),
      );
      await expectLater(slow.add(account()), throwsA(isA<VaultAuthFailed>()));
      expect(await slow.list(), isEmpty, reason: 'metadata calls still run');
    });
  });

  group('secrets are wiped', () {
    test('after add and after read', () async {
      final r = Rig();
      final a = account(seed: 1);
      final info = await r.vault.add(a);
      expect(r.keys.sealedPlain.last.every((b) => b == 0), isTrue,
          reason: 'the copy handed to the keystore is zeroed');
      expect(a.isWiped, isFalse,
          reason: 'the caller keeps ownership of its own');
      final back = await r.vault.readSecret(info.id);
      expect(r.keys.openedPlain.last.every((b) => b == 0), isTrue,
          reason: 'the buffer the keystore returned is zeroed');
      back.wipe();
    });

    test('a refused account leaves nothing and no secret is in a message',
        () async {
      final r = Rig();
      try {
        await r.vault.add(account(seed: 1));
        await r.vault.readSecret('missing');
      } on ArgumentError catch (e) {
        expect(e.toString(), isNot(contains('GitHub')));
      }
      for (final e in const [
        VaultBiometricsChanged(),
        VaultAuthFailed(),
        VaultCorrupt('The vault is damaged.'),
        VaultRolledBack(),
      ]) {
        expect(e.toString(), isNot(contains('alice')));
      }
    });
  });

  group('platform channel', () {
    const channel = MethodChannel('io.biatec.authenticator/vault');
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

    tearDown(() => messenger.setMockMethodCallHandler(channel, null));

    Future<Object?> call(String code) async {
      messenger.setMockMethodCallHandler(channel, (c) async {
        throw PlatformException(code: code, message: 'key alias biatec.vault');
      });
      try {
        await const PlatformVaultKeyStore()
            .open(VaultKeyPurpose.secret, Uint8List(40), Uint8List(1));
      } on VaultException catch (e) {
        return e;
      }
      return null;
    }

    test('error codes become the vault failures, without the platform text',
        () async {
      expect(await call('biometrics_changed'), isA<VaultBiometricsChanged>());
      expect(await call('auth_failed'), isA<VaultAuthFailed>());
      expect(await call('bad_data'), isA<VaultCorrupt>());
      final other = await call('keystore_error') as VaultException;
      expect(other.toString(), isNot(contains('alias')));
    });

    test('purpose, data and aad are sent', () async {
      MethodCall? seen;
      messenger.setMockMethodCallHandler(channel, (c) async {
        seen = c;
        return Uint8List.fromList([1, 2, 3]);
      });
      final out = await const PlatformVaultKeyStore().seal(
          VaultKeyPurpose.metadata,
          Uint8List.fromList([9]),
          Uint8List.fromList([8]));
      expect(out, [1, 2, 3]);
      expect(seen!.method, 'seal');
      expect((seen!.arguments as Map)['purpose'], 'metadata');
    });
  });

  group('native key flags', () {
    // The native code cannot run here, so these read its source: the flags that make the key biometric-bound are part of the contract.
    String read(String p) => File(p).readAsStringSync();

    test(
        'Android: AES-256-GCM in the Keystore, biometric-bound and invalidated',
        () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/VaultPlugin.kt');
      expect(kt, contains('io.biatec.authenticator/vault'));
      expect(kt, contains('KEY_ALGORITHM_AES'));
      expect(kt, contains('BLOCK_MODE_GCM'));
      expect(kt, contains('setKeySize(256)'));
      expect(kt, contains('setUserAuthenticationRequired(true)'));
      expect(kt, contains('setInvalidatedByBiometricEnrollment(true)'));
      expect(kt, contains('AUTH_BIOMETRIC_STRONG'));
      expect(kt, contains('BIOMETRIC_STRONG'));
      expect(kt, contains('setIsStrongBoxBacked(true)'));
      expect(kt, contains('KeyPermanentlyInvalidatedException'));
      expect(kt, contains('biometrics_changed'));
      expect(kt, contains('setRandomizedEncryptionRequired(true)'));
      expect(kt, contains('updateAAD'));
      expect(kt, isNot(contains('DEVICE_CREDENTIAL')),
          reason: 'a screen lock PIN is not a strong biometric');
      final activity = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt');
      expect(activity, contains('VaultPlugin('));
    });

    test('iOS: Secure Enclave key, current biometric set, this device only',
        () {
      final sw = read('ios/Runner/VaultPlugin.swift');
      expect(sw, contains('io.biatec.authenticator/vault'));
      expect(sw, contains('kSecAttrTokenIDSecureEnclave'));
      expect(sw, contains('biometryCurrentSet'));
      expect(sw, contains('ThisDeviceOnly'));
      expect(sw, contains('eciesEncryptionCofactorVariableIVX963SHA256AESGCM'));
      expect(sw, contains('biometrics_changed'));
      expect(sw, isNot(contains('kSecAttrAccessibleAlways')));
      final delegate = read('ios/Runner/AppDelegate.swift');
      expect(delegate, contains('VaultPlugin.register('));
    });

    test('every object id in the iOS project is defined once', () {
      final project = read('ios/Runner.xcodeproj/project.pbxproj');
      final ids = RegExp(r'^		([0-9A-F]{24}) /\*', multiLine: true)
          .allMatches(project)
          .map((m) => m.group(1)!)
          .toList();
      expect(ids, isNotEmpty);
      expect(ids.toSet().length, ids.length,
          reason: 'a repeated id makes Xcode drop one of the objects');
      expect(read('ios/Runner.xcodeproj/project.pbxproj'),
          contains('VaultPlugin.swift in Sources'));
    });

    test('the Android plugin answers once, off the main thread', () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/VaultPlugin.kt');
      expect(kt, contains('AtomicBoolean'));
      expect(kt, contains('Executors.newSingleThreadExecutor'));
      expect(kt, contains('NoBiometricsException'));
      expect(read('ios/Runner/VaultPlugin.swift'),
          contains('LAError.errorDomain'));
    });

    test('the vault file is kept out of backups', () {
      final manifest = read('android/app/src/main/AndroidManifest.xml');
      expect(manifest, contains('android:allowBackup="false"'));
      expect(read('ios/Runner/AppDelegate.swift'),
          contains('isExcludedFromBackup = true'));
    });
  });
}

/// A key store whose secret prompt never answers.
class _Hanging implements VaultKeyStore {
  _Hanging(this.inner);
  final FakeVaultKeyStore inner;

  @override
  Future<Uint8List> seal(
      VaultKeyPurpose purpose, Uint8List plaintext, Uint8List aad) {
    if (purpose == VaultKeyPurpose.secret) return Completer<Uint8List>().future;
    return inner.seal(purpose, plaintext, aad);
  }

  @override
  Future<Uint8List> open(
          VaultKeyPurpose purpose, Uint8List sealed, Uint8List aad) =>
      inner.open(purpose, sealed, aad);

  @override
  Future<void> endSession() => inner.endSession();

  @override
  Future<void> deleteKeys() => inner.deleteKeys();
}
