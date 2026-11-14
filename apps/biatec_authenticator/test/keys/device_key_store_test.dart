// BCHAT-032: the key store reads where the native keystore says the key lives, and a missing or unknown value is never read as hardware.
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
      expect(asked, ['approve', 'presence']);
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
