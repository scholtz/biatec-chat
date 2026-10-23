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

  group('PlatformDeviceKeyStore', () {
    const store = PlatformDeviceKeyStore();

    test('generate and load carry the level of the key', () async {
      answer((call) => {'x': 'xx', 'y': 'yy', 'securityLevel': 'strongbox'});
      expect((await store.generate('a')).securityLevel,
          KeySecurityLevel.strongBox);
      expect(
          (await store.load('a'))!.securityLevel, KeySecurityLevel.strongBox);
    });

    test('a plugin that does not report the level gives unknown, not hardware',
        () async {
      answer((call) => {'x': 'xx', 'y': 'yy'});
      final key = await store.generate('a');
      expect(key.securityLevel, KeySecurityLevel.unknown);
      expect(key.securityLevel.isHardware, isFalse);
    });

    test('a level this app does not know is unknown', () async {
      answer((call) => {'x': 'xx', 'y': 'yy', 'securityLevel': 'quantum'});
      expect(
          (await store.generate('a')).securityLevel, KeySecurityLevel.unknown);
    });

    test('load of a missing key is null', () async {
      answer((call) => null);
      expect(await store.load('a'), isNull);
    });

    test('a refusal from the plugin reaches the caller', () async {
      messenger.setMockMethodCallHandler(channel, (call) async {
        throw PlatformException(
            code: 'insecure_key',
            message: 'The keystore holds the key in software');
      });
      expect(store.generate('a'), throwsA(isA<PlatformException>()));
    });
  });
}
