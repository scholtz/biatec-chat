import 'dart:io';

import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/security/biometric_policy.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

String read(String path) => File(path).readAsStringSync();

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('io.biatec.authenticator/biometric');
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  tearDown(() => messenger.setMockMethodCallHandler(channel, null));

  Future<BiometricOutcome> outcome(Object? answer) async {
    messenger.setMockMethodCallHandler(channel, (c) async {
      if (answer is Exception) throw answer;
      return answer;
    });
    return PlatformBiometricAuthenticator().authenticate('Unlock');
  }

  group('what the platform answers', () {
    test('only "success" is a success', () async {
      expect(await outcome('success'), BiometricOutcome.success);
      for (final other in [
        'cancelled',
        'failed',
        'lockout',
        'none_enrolled',
        'unavailable',
        'SUCCESS',
        'ok',
        'true',
        '',
        null,
        true,
        1,
      ]) {
        expect(await outcome(other), isNot(BiometricOutcome.success),
            reason: '$other');
      }
    });

    test('an error is a failure, never a success', () async {
      expect(
          await outcome(PlatformException(code: 'x')), BiometricOutcome.failed);
      expect(await outcome(MissingPluginException()), BiometricOutcome.failed);
    });

    test('the answers map to their outcomes', () async {
      expect(await outcome('cancelled'), BiometricOutcome.cancelled);
      expect(await outcome('lockout'), BiometricOutcome.lockout);
      expect(await outcome('none_enrolled'), BiometricOutcome.noneEnrolled);
      expect(await outcome('unavailable'), BiometricOutcome.unavailable);
    });

    test('the status answers, and an unknown one is not available', () async {
      Future<BiometricStatus> status(Object? answer) async {
        messenger.setMockMethodCallHandler(channel, (c) async => answer);
        return PlatformBiometricAuthenticator().status();
      }

      expect(await status('available'), BiometricStatus.available);
      expect(await status('none_enrolled'), BiometricStatus.noneEnrolled);
      expect(await status('no_hardware'), BiometricStatus.noHardware);
      expect(await status('unavailable'), BiometricStatus.unavailable);
      expect(await status('weak'), BiometricStatus.unavailable);
      expect(await status(null), BiometricStatus.unavailable);
    });

    test('the reason is sent, and a screen lock event reaches Dart', () async {
      MethodCall? seen;
      messenger.setMockMethodCallHandler(channel, (c) async {
        seen = c;
        return 'success';
      });
      final auth = PlatformBiometricAuthenticator();
      await auth.authenticate('Approve this action');
      expect((seen!.arguments as Map)['reason'], 'Approve this action');

      var locked = 0;
      final sub = auth.screenLocked.listen((_) => locked++);
      await messenger.handlePlatformMessage(
          channel.name,
          const StandardMethodCodec()
              .encodeMethodCall(const MethodCall('screenLocked')),
          (ByteData? data) {});
      await Future<void>.delayed(Duration.zero);
      expect(locked, 1);
      await sub.cancel();
    });
  });

  group('no weaker way in: the policy is strong biometric only', () {
    final kotlin = read(
        'android/app/src/main/kotlin/io/biatec/authenticator/BiometricChannel.kt');
    final swift = read('ios/Runner/BiometricChannel.swift');
    // Without comments: the files explain what they refuse.
    String code(String s) => s
        .split('\n')
        .where((l) =>
            !l.trimLeft().startsWith('//') &&
            !l.trimLeft().startsWith('*') &&
            !l.trimLeft().startsWith('/*'))
        .join('\n');

    test('Android: BiometricPrompt with BIOMETRIC_STRONG and no credential',
        () {
      final c = code(kotlin);
      expect(c, contains('BIOMETRIC_STRONG'));
      expect(c, contains('setAllowedAuthenticators(BIOMETRIC_STRONG)'));
      expect(c, isNot(contains('DEVICE_CREDENTIAL')));
      expect(c, isNot(contains('BIOMETRIC_WEAK')));
      expect(c, isNot(contains('setDeviceCredentialAllowed')));
      expect(c, contains('setNegativeButtonText'),
          reason: 'a Cancel button, not a "use PIN" button');
    });

    test('iOS: the biometrics-only policy, never the passcode one', () {
      final c = code(swift);
      expect(c, contains('deviceOwnerAuthenticationWithBiometrics'));
      expect(RegExp(r'deviceOwnerAuthentication(?!WithBiometrics)').hasMatch(c),
          isFalse,
          reason: 'the plain policy falls back to the passcode');
      expect(c, contains('localizedFallbackTitle = ""'));
    });

    test('no Dart code asks for a weaker check, and local_auth is gone', () {
      final pubspec = read('pubspec.yaml');
      expect(pubspec, isNot(contains('local_auth')));
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final text = f.readAsStringSync();
        expect(text, isNot(contains('package:local_auth')), reason: f.path);
        expect(text, isNot(contains('biometricOnly: false')), reason: f.path);
        expect(text, isNot(contains('deviceCredential')), reason: f.path);
        expect(text, isNot(contains('DEVICE_CREDENTIAL')), reason: f.path);
      }
    });

    test('the only way to the unlocked state is a biometric success', () {
      final text = read('lib/lock/lock_controller.dart');
      // The state is set to unlocked in exactly one place, under the success case.
      final sets = RegExp(r'_set\(AppLockState\.unlocked\)').allMatches(text);
      expect(sets.length, 1);
      final at = sets.single.start;
      expect(
          text.substring(at - 120, at), contains('BiometricOutcome.success'));
      expect(text, isNot(contains('debugUnlock')));
      expect(text, isNot(contains('kDebugMode')));
      expect(text, isNot(contains('kReleaseMode')));
      expect(text, isNot(contains('assert(')));
    });

    test('the secure area is built only inside the lock gate', () {
      final gate = read('lib/shell/app_gate.dart');
      expect(gate, contains('AppLockGate('));
      final at = gate.indexOf('AppLockGate(');
      expect(gate.indexOf('AppShell(', at), greaterThan(at));
      expect(gate.indexOf('AppShell('), greaterThan(at),
          reason: 'no AppShell is built outside the gate');
    });

    test('a tapped notification keeps its id in memory only', () {
      final shell = read('lib/shell/shell_controller.dart');
      expect(shell, isNot(contains('secure_storage')));
      expect(shell, isNot(contains('SharedPreferences')));
      expect(shell, isNot(contains('writeAsString')));
    });

    test('approvals and settings changes ask through the lock', () {
      expect(read('lib/approvals/approval_services.dart'),
          isNot(contains('LocalAuth')));
      expect(read('lib/approvals/approval_controller.dart'),
          contains('gate.confirm('));
      expect(read('lib/security/settings_controller.dart'),
          contains('gate.confirm('));
      final main = read('lib/main.dart');
      expect(main, contains('LockBiometricGate(lock)'));
      expect('lock: lock'.allMatches(main).length, 2,
          reason: 'both ways of starting the app share the one lock');
      expect(read('lib/shell/app_gate.dart'), contains('forgetRecoveryCode'));
    });

    test('the channel is wired on both platforms', () {
      expect(
          read(
              'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt'),
          contains('BiometricChannel('));
      expect(read('ios/Runner/AppDelegate.swift'),
          contains('BiometricChannel.register('));
      final project = read('ios/Runner.xcodeproj/project.pbxproj');
      expect('BiometricChannel.swift in Sources */,'.allMatches(project).length,
          1);
    });
  });
}
