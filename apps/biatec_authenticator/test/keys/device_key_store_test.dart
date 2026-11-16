// BCHAT-032: the key store reads where the native keystore says the key lives, and a missing or unknown value is never read as hardware.
import 'dart:io';

import 'package:biatec_authenticator/keys/device_key_store.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('io.biatec.authenticator/keystore');
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  void answer(Object? Function(MethodCall call) handler) => messenger
      .setMockMethodCallHandler(channel, (call) async => handler(call));

  tearDown(() => messenger.setMockMethodCallHandler(channel, null));

  group('KeySecurityLevel', () {
    test('hardware levels are hardware, software and unknown are not', () {
      for (final level in KeySecurityLevel.values) {
        expect(
          level.isHardware,
          level != KeySecurityLevel.software &&
              level != KeySecurityLevel.unknown,
          reason: level.wire,
        );
      }
    });

    test('the wire values round-trip and anything else is unknown', () {
      for (final level in KeySecurityLevel.values) {
        expect(KeySecurityLevel.fromWire(level.wire), level);
      }
      for (final value in [null, '', 'STRONGBOX', 'hardware-backed', 7, true]) {
        expect(KeySecurityLevel.fromWire(value), KeySecurityLevel.unknown,
            reason: '$value');
      }
    });

    test('every level has a label for the user', () {
      for (final level in KeySecurityLevel.values) {
        expect(level.label, isNotEmpty);
      }
    });
  });

  Map<String, Object?> part(String level, {bool bound = false}) =>
      {'x': 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU', 'y': 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0', 'securityLevel': level, 'authBound': bound};

  group('PlatformDeviceKeyStore', () {
    const store = PlatformDeviceKeyStore();

    test('generateKeys carries the level and the auth binding of each key',
        () async {
      answer((call) => {
            'approval': part('strongbox', bound: true),
            'presence': part('tee'),
          });
      final keys = await store.generateKeys();
      expect(keys.approval.securityLevel, KeySecurityLevel.strongBox);
      expect(keys.approval.authBound, isTrue);
      expect(keys.presence.securityLevel, KeySecurityLevel.trustedEnvironment);
      expect(keys.presence.authBound, isFalse);
      expect(keys.approval.kid, startsWith('biatec-approve-'));
      expect(keys.presence.kid, startsWith('biatec-presence-'));
      expect(keys.approval.kid, isNot(keys.presence.kid));
    });

    test('only the approval key can be auth bound, and only when the plugin says true',
        () async {
      answer((call) => {
            'approval': part('tee'),
            'presence': part('tee', bound: true),
          });
      final keys = await store.generateKeys();
      expect(keys.approval.authBound, isFalse, reason: 'not reported');
      expect(keys.presence.authBound, isFalse, reason: 'never for presence');
    });

    test('a plugin that does not report the level gives unknown, not hardware',
        () async {
      answer((call) => {
            'approval': {'x': 'xx', 'y': 'yy'},
            'presence': {'x': 'xx', 'y': 'yy'},
          });
      final key = (await store.generateKeys()).approval;
      expect(key.securityLevel, KeySecurityLevel.unknown);
      expect(key.securityLevel.isHardware, isFalse);
    });

    test('load asks for one use and a missing key is null', () async {
      final asked = <Object?>[];
      answer((call) {
        asked.add((call.arguments as Map)['use']);
        return null;
      });
      expect(await store.loadApproval(), isNull);
      expect(await store.loadPresence(), isNull);
      // The CHANNEL vocabulary (approval, presence), not the protocol's key use (approve, presence).
      expect(asked, ['approval', 'presence']);
    });

    test('the exact method names and arguments the Dart side sends', () async {
      final calls = <MethodCall>[];
      answer((call) {
        calls.add(call);
        switch (call.method) {
          case 'generateKeys':
            return {'approval': part('tee', bound: true), 'presence': part('tee')};
          case 'keysState':
            return 'ok';
          case 'signPresence':
          case 'signApproval':
            return Uint8List(64);
        }
        return null;
      });
      await store.generateKeys();
      await store.loadApproval();
      await store.loadPresence();
      await store.keysState();
      await store.signPresence(Uint8List(1));
      await store.signApproval(Uint8List(1), promptTitle: 't', promptSubtitle: 's');
      await store.deleteKeys();
      await store.deleteLegacyKey('old-alias');
      expect([for (final c in calls) '${c.method} ${c.arguments is Map ? ((c.arguments as Map).keys.toList()..sort()).join(',') : ''}'], [
        'generateKeys ',
        'load use',
        'load use',
        'keysState ',
        'signPresence data',
        'signApproval data,subtitle,title',
        'deleteKeys ',
        'delete alias',
      ]);
      expect([for (final c in calls.where((c) => c.method == 'load')) (c.arguments as Map)['use']], ['approval', 'presence']);
    });

    test('KeystorePlugin.kt and KeystorePlugin.swift understand every name and value the Dart side sends', () {
      final kt = File('android/app/src/main/kotlin/io/biatec/authenticator/KeystorePlugin.kt').readAsStringSync();
      final swift = File('ios/Runner/KeystorePlugin.swift').readAsStringSync();
      for (final method in ['generateKeys', 'generatePresence', 'load', 'loadLegacy', 'signLegacy', 'keysState', 'signPresence', 'signApproval', 'deleteKeys', 'delete']) {
        expect(kt, contains('"$method"'), reason: 'Kotlin: $method');
        expect(swift, contains('"$method"'), reason: 'Swift: $method');
      }
      for (final use in [channelUse(KeyUse.approve), channelUse(KeyUse.presence)]) {
        expect(kt, contains('"$use" ->'), reason: 'Kotlin load use $use');
        expect(swift, contains('case "$use"'), reason: 'Swift load use $use');
      }
      for (final arg in ['use', 'data', 'title', 'subtitle', 'alias']) {
        expect(kt, contains('"$arg"'), reason: 'Kotlin argument $arg');
        expect(swift, contains('"$arg"'), reason: 'Swift argument $arg');
      }
      // What the plugins answer: the keys state, the key map and the error codes Dart maps.
      for (final wire in ['ok', 'approval_key_invalidated', 'missing']) {
        expect(KeysState.fromWire(wire).wire, wire);
        expect(kt, contains('"$wire"'));
        expect(swift, contains('"$wire"'));
      }
      // Single-key mode makes ONLY the prompt-free key: no approval key, no biometric requirement, on both platforms.
      final ktGen = kt.substring(kt.indexOf('private fun generatePresence'));
      expect(ktGen.substring(0, ktGen.indexOf('private fun signWith')), isNot(contains('APPROVAL_ALIAS')));
      final swGen = swift.substring(swift.indexOf('private func generatePresence'));
      expect(swGen.substring(0, swGen.indexOf('// One approval prompt at a time')), isNot(contains('approvalTag')));
      // Single-key mode keeps the key usable while the phone is locked (the stay-connected service signs then); the two-key keys need an unlocked phone.
      expect(ktGen.substring(0, ktGen.indexOf('private fun signWith')), contains('unlocked = false'));
      expect(kt, contains('if (unlocked && Build.VERSION.SDK_INT'));
      expect(kt, contains('make(PRESENCE_ALIAS, false)\n'), reason: 'the two-key presence key keeps the default (unlocked required)');
      // keysState does not leave a probe operation open on every call (it is cached).
      expect(kt, contains('STATE_TTL_MS'));
      // One approval prompt at a time, on both platforms.
      expect(kt, contains('promptActive'));
      expect(swift, contains('promptActive'));
      // The old alias of an install from before the two keys is still looked up (and never the new aliases).
      expect(kt, contains('legacyAlias(call)'));
      expect(swift, contains('legacyAlias(args)'));
      for (final code in ['prompt_active', 'cancelled', 'approval_key_invalidated', 'no_biometrics', 'locked_out', 'auth_failed', 'key_generation_failed', 'insecure_key']) {
        expect(kt, contains('"$code"'), reason: 'Kotlin error $code');
        expect(swift, contains('"$code"'), reason: 'Swift error $code');
      }
    });

    test('key_channels.cpp (Windows) understands every name and value the Dart side sends and answers what Dart reads', () {
      final cpp = File('windows/runner/key_channels.cpp').readAsStringSync();
      final keystoreCpp = File('windows/runner/keystore.cpp').readAsStringSync();
      for (final method in ['generateKeys', 'generatePresence', 'load', 'loadLegacy', 'signLegacy', 'keysState', 'signPresence', 'signApproval', 'deleteKeys', 'delete', 'probe']) {
        expect(cpp, contains('"$method"'), reason: 'C++: $method');
      }
      for (final use in [channelUse(KeyUse.approve), channelUse(KeyUse.presence)]) {
        expect(cpp, contains('"$use"'), reason: 'C++ load use $use');
      }
      for (final arg in ['use', 'data', 'title', 'subtitle']) {
        expect(cpp, contains('"$arg"'), reason: 'C++ argument $arg');
      }
      for (final wire in ['ok', 'approval_key_invalidated', 'missing']) {
        expect(cpp, contains('"$wire"'), reason: 'C++ keys state $wire');
      }
      // The level it reports is one Dart knows, and as hardware only when the provider says so.
      expect(KeySecurityLevel.fromWire('tpm'), KeySecurityLevel.tpm);
      expect(KeySecurityLevel.tpm.isHardware, isTrue);
      expect(cpp, contains('key.hardware ? "tpm" : "software"'));
      for (final code in ['prompt_active', 'cancelled', 'approval_key_invalidated', 'no_biometrics', 'locked_out', 'auth_failed', 'insecure_key', 'no_tpm', 'no_hello']) {
        expect(cpp, contains('"$code"'), reason: 'C++ error $code');
      }
      expect(keystoreCpp, contains('"key_generation_failed"'));
      // Both keys are P-256 keys of the TPM's provider, not exportable (no export policy is ever set), and the approval signature runs only after the Windows Hello
      // check said verified, inside the one native call.
      expect(keystoreCpp, contains('MS_PLATFORM_CRYPTO_PROVIDER'));
      expect(keystoreCpp, contains('BCRYPT_ECDSA_P256_ALGORITHM'));
      expect(keystoreCpp, isNot(contains('NCRYPT_ALLOW_EXPORT_FLAG')));
      expect(keystoreCpp, isNot(contains('NCRYPT_ALLOW_PLAINTEXT')));
      final approval = cpp.substring(cpp.indexOf('EncodableValue SignApproval('));
      final approvalBody = approval.substring(0, approval.indexOf('// The app lock'));
      expect(approvalBody.indexOf('hello::Verify'), greaterThan(-1));
      expect(approvalBody.indexOf('hello::Verify'), lessThan(approvalBody.indexOf('keystore::Sign(')), reason: 'the check comes before the signature');
      expect(approvalBody, contains('case hello::Outcome::kVerified:\n      break;'), reason: 'only "verified" reaches the signature');
    });

    test('status reads the probe of a Windows build: TPM and Windows Hello', () async {
      for (final (answerOf, expected) in [
        ({'tpm': true, 'hello': 'available'}, KeyStoreStatus.ready),
        ({'tpm': false, 'hello': 'available'}, KeyStoreStatus.noTpm),
        ({'tpm': true, 'hello': 'not_configured'}, KeyStoreStatus.noHello),
        ({'tpm': true, 'hello': 'no_device'}, KeyStoreStatus.noHello),
        ({'tpm': true, 'hello': 'something new'}, KeyStoreStatus.noHello),
        ({'hello': 'available'}, KeyStoreStatus.noTpm),
        ({'tpm': 'yes', 'hello': 'available'}, KeyStoreStatus.noTpm),
      ]) {
        answer((call) => call.method == 'probe' ? answerOf : null);
        expect(await store.status(), expected, reason: '$answerOf');
      }
    });

    test('status on a phone (a plugin without probe) is ready, and on a platform without a key store it throws', () async {
      answer((call) {
        if (call.method == 'probe') throw MissingPluginException('no probe');
        return 'missing';
      });
      expect(await store.status(), KeyStoreStatus.ready);
      answer((call) => throw MissingPluginException('no plugin'));
      await expectLater(store.status(), throwsA(isA<MissingPluginException>()));
    });

    test('keysState maps the plugin answer and fails closed', () async {
      for (final (wire, state) in [
        ('ok', KeysState.ok),
        ('approval_key_invalidated', KeysState.approvalKeyInvalidated),
        ('missing', KeysState.missing),
        ('anything else', KeysState.missing),
        (null, KeysState.missing),
      ]) {
        answer((call) => wire);
        expect(await store.keysState(), state, reason: '$wire');
      }
    });

    test('signPresence never carries prompt text; signApproval does', () async {
      final calls = <MethodCall>[];
      answer((call) {
        calls.add(call);
        return Uint8List(64);
      });
      await store.signPresence(Uint8List(3));
      await store.signApproval(Uint8List(3),
          promptTitle: 'Approve this action', promptSubtitle: 'Bank: 10 EUR');
      expect(calls[0].method, 'signPresence');
      expect((calls[0].arguments as Map).keys, ['data']);
      expect(calls[1].method, 'signApproval');
      expect((calls[1].arguments as Map)['title'], 'Approve this action');
      expect((calls[1].arguments as Map)['subtitle'], 'Bank: 10 EUR');
    });

    test('prompt failures become typed exceptions', () async {
      Future<Object?> run(String code) async {
        messenger.setMockMethodCallHandler(
            channel, (call) async => throw PlatformException(code: code, message: 'm'));
        try {
          await store.signApproval(Uint8List(3), promptTitle: 't', promptSubtitle: 's');
        } on Object catch (e) {
          return e;
        }
        return null;
      }

      expect(await run('cancelled'), isA<KeyPromptCancelled>());
      expect(await run('approval_key_invalidated'), isA<ApprovalKeyInvalidated>());
      for (final code in ['no_biometrics', 'locked_out', 'no_activity', 'auth_failed']) {
        final e = await run(code);
        expect(e, isA<KeyPromptFailed>(), reason: code);
        expect((e as KeyPromptFailed).code, code);
      }
      expect(await run('keystore_error'), isA<PlatformException>());
    });

    test('the kid is derived from the key (RFC 7638 thumbprint) and stable', () {
      final a = DeviceKey.fromParts(KeyUse.approve,
          x: 'f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU',
          y: 'x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0',
          securityLevel: KeySecurityLevel.strongBox);
      expect(a.thumbprint, hasLength(43));
      expect(a.kid, 'biatec-approve-${a.thumbprint.substring(0, 32)}');
    });

    test('a refusal from the plugin reaches the caller', () async {
      messenger.setMockMethodCallHandler(channel, (call) async {
        throw PlatformException(
            code: 'insecure_key',
            message: 'The keystore holds the key in software');
      });
      expect(store.generateKeys(), throwsA(isA<PlatformException>()));
    });
  });
}
