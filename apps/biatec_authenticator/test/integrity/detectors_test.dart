import 'package:biatec_authenticator/integrity/detectors.dart';
import 'package:biatec_authenticator/integrity/integrity.dart';
import 'package:biatec_authenticator/integrity/integrity_checker.dart';
import 'package:flutter_test/flutter_test.dart';

/// A phone made of fixtures: which files exist, what can be written, what /proc says, which properties are set.
class FakeEnvironment implements IntegrityEnvironment {
  FakeEnvironment(this.platform);

  @override
  final IntegrityPlatform platform;

  final files = <String>{};
  final writable = <String>{};
  final contents = <String, String>{};
  final properties = <String, String>{};
  final classes = <String>{};
  final images = <String>[];
  bool debugger = false;
  bool simulator = false;
  String? digest;

  @override
  bool fileExists(String path) => files.contains(path);

  @override
  bool canWriteTo(String directory) => writable.contains(directory);

  @override
  String? readFile(String path) => contents[path];

  @override
  Future<String?> property(String key) async => properties[key];

  @override
  Future<bool> debuggerAttached() async => debugger;

  @override
  Future<bool> classPresent(String name) async => classes.contains(name);

  @override
  Future<List<String>> loadedImages() async => images;

  @override
  Future<bool> isSimulator() async => simulator;

  @override
  Future<String?> signingDigest() async => digest;
}

const release =
    'AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89';
final releaseHex = release.replaceAll(':', '').toLowerCase();

FakeEnvironment healthyAndroid() => FakeEnvironment(IntegrityPlatform.android)
  ..properties.addAll({
    'ro.build.tags': 'release-keys',
    'ro.debuggable': '0',
    'ro.secure': '1',
    'ro.hardware': 'qcom',
    'ro.build.fingerprint': 'google/oriole/oriole:14/release-keys',
    'ro.product.model': 'Pixel 6',
  })
  ..contents['/proc/self/status'] = 'Name:\tbiatec\nTracerPid:\t0\n'
  ..contents['/proc/self/maps'] =
      '7f00-7f01 r-xp /system/lib64/libc.so\n7f02-7f03 r-xp /data/app/io.biatec/lib/libflutter.so\n'
  ..digest = releaseHex;

FakeEnvironment healthyIos() => FakeEnvironment(IntegrityPlatform.ios)
  ..images.addAll([
    '/usr/lib/libSystem.B.dylib',
    '/System/Library/Frameworks/UIKit.framework/UIKit'
  ]);

void main() {
  group('a healthy phone', () {
    test('Android: no detector fires', () async {
      final result = await DeviceIntegrityChecker(
              environment: healthyAndroid(), expectedSigningDigest: release)
          .check();
      expect(result.compromised, isFalse);
      expect(result.reasons, isEmpty);
    });

    test('iOS: no detector fires', () async {
      final result = await DeviceIntegrityChecker(
              environment: healthyIos(), expectedSigningDigest: '')
          .check();
      expect(result.compromised, isFalse);
    });
  });

  group('Android root', () {
    const d = AndroidRootDetector();

    for (final path in [
      ...AndroidRootDetector.suPaths,
      ...AndroidRootDetector.managerPaths
    ]) {
      test('a file at $path', () async {
        expect(await d.triggered(healthyAndroid()..files.add(path)), isTrue);
      });
    }

    for (final folder in AndroidRootDetector.protectedFolders) {
      test('a writable $folder', () async {
        expect(
            await d.triggered(healthyAndroid()..writable.add(folder)), isTrue);
      });
    }

    test('test-keys, a debuggable build and an insecure build', () async {
      expect(
          await d.triggered(healthyAndroid()
            ..properties['ro.build.tags'] = 'dev-keys,test-keys'),
          isTrue);
      expect(
          await d
              .triggered(healthyAndroid()..properties['ro.debuggable'] = '1'),
          isTrue);
      expect(await d.triggered(healthyAndroid()..properties['ro.secure'] = '0'),
          isTrue);
    });

    test('a healthy phone is left alone', () async {
      expect(await d.triggered(healthyAndroid()), isFalse);
    });
  });

  group('iOS jailbreak', () {
    const d = IosJailbreakDetector();

    for (final path in IosJailbreakDetector.paths) {
      test('a file at $path', () async {
        expect(await d.triggered(healthyIos()..files.add(path)), isTrue);
      });
    }

    test('an open sandbox', () async {
      expect(await d.triggered(healthyIos()..writable.add('/private')), isTrue);
    });

    test('a healthy phone is left alone', () async {
      expect(await d.triggered(healthyIos()), isFalse);
    });
  });

  group('debugger', () {
    const d = DebuggerDetector();

    test('attached, as the platform reports', () async {
      expect(await d.triggered(healthyAndroid()..debugger = true), isTrue);
      expect(await d.triggered(healthyIos()..debugger = true), isTrue);
    });

    test('Android: a tracer in /proc, but not TracerPid 0', () async {
      expect(
          await d.triggered(healthyAndroid()
            ..contents['/proc/self/status'] = 'TracerPid:\t4242\n'),
          isTrue);
      expect(await d.triggered(healthyAndroid()), isFalse);
      expect(
          await d.triggered(
              healthyAndroid()..contents.remove('/proc/self/status')),
          isFalse,
          reason: 'an unreadable file says nothing');
    });

    test('a healthy iOS phone', () async {
      expect(await d.triggered(healthyIos()), isFalse);
    });
  });

  group('hooking', () {
    const d = HookingDetector();

    for (final marker in [
      'frida-agent-64.so',
      'libfrida-gadget.so',
      'gum-js-loop',
      'libsubstrate.so',
      'XposedBridge.jar',
      'liblspd.so'.replaceAll('lspd', 'lsposed'),
      'riru.so',
      'zygisk'
    ]) {
      test('Android: $marker in the process maps', () async {
        final env = healthyAndroid()
          ..contents['/proc/self/maps'] =
              '7f00-7f01 r-xp /data/local/tmp/$marker\n';
        expect(await d.triggered(env), isTrue);
      });
    }

    test('Android: a hooking class, a Frida server file', () async {
      for (final c in HookingDetector.classNames) {
        expect(await d.triggered(healthyAndroid()..classes.add(c)), isTrue,
            reason: c);
      }
      for (final f in HookingDetector.androidFiles) {
        expect(await d.triggered(healthyAndroid()..files.add(f)), isTrue,
            reason: f);
      }
    });

    for (final image in [
      '/usr/lib/FridaGadget.dylib',
      '/Library/MobileSubstrate/MobileSubstrate.dylib',
      '/usr/lib/libhooker.dylib',
      '/usr/lib/cycript0.9/libcycript.dylib',
      '/usr/lib/TweakInject.dylib'
    ]) {
      test('iOS: $image loaded', () async {
        expect(await d.triggered(healthyIos()..images.add(image)), isTrue);
      });
    }

    test('healthy phones', () async {
      expect(await d.triggered(healthyAndroid()), isFalse);
      expect(await d.triggered(healthyIos()), isFalse);
    });
  });

  group('emulator', () {
    const d = EmulatorDetector();

    test('Android properties', () async {
      expect(
          await d
              .triggered(healthyAndroid()..properties['ro.kernel.qemu'] = '1'),
          isTrue);
      expect(
          await d.triggered(
              healthyAndroid()..properties['ro.hardware'] = 'ranchu'),
          isTrue);
      expect(
          await d.triggered(
              healthyAndroid()..properties['ro.hardware'] = 'goldfish'),
          isTrue);
      expect(
          await d.triggered(healthyAndroid()
            ..properties['ro.build.fingerprint'] = 'generic/sdk/generic:11'),
          isTrue);
      expect(
          await d.triggered(healthyAndroid()
            ..properties['ro.product.model'] = 'Android SDK built for x86'),
          isTrue);
      expect(await d.triggered(healthyAndroid()), isFalse);
    });

    test('the iOS simulator', () async {
      expect(await d.triggered(healthyIos()..simulator = true), isTrue);
      expect(await d.triggered(healthyIos()), isFalse);
    });
  });

  group('signing certificate', () {
    test('matches whatever the case and separators of the configured digest',
        () async {
      for (final configured in [
        release,
        releaseHex,
        releaseHex.toUpperCase(),
        release.toLowerCase()
      ]) {
        expect(
            await SigningCertificateDetector(configured)
                .triggered(healthyAndroid()),
            isFalse,
            reason: configured);
      }
    });

    test('a different certificate is compromised', () async {
      final env = healthyAndroid()..digest = '00' * 32;
      expect(await SigningCertificateDetector(release).triggered(env), isTrue);
    });

    test('no certificate readable is compromised', () async {
      expect(
          await SigningCertificateDetector(release)
              .triggered(healthyAndroid()..digest = null),
          isTrue);
    });

    test(
        'nothing configured is compromised: a release build must say what it is',
        () async {
      expect(
          await const SigningCertificateDetector('')
              .triggered(healthyAndroid()),
          isTrue);
    });

    test('the checker applies it on Android only', () async {
      final ios = await DeviceIntegrityChecker(
              environment: healthyIos(), expectedSigningDigest: '')
          .check();
      expect(ios.compromised, isFalse);
      final android = await DeviceIntegrityChecker(
              environment: healthyAndroid()..digest = '11' * 32,
              expectedSigningDigest: release)
          .check();
      expect(android.reasons, {IntegrityReason.appNotGenuine});
    });
  });

  group('the checker', () {
    test('gathers every group that fired', () async {
      final env = healthyAndroid()
        ..files.add('/system/xbin/su')
        ..debugger = true
        ..properties['ro.kernel.qemu'] = '1'
        ..digest = '22' * 32;
      final result = await DeviceIntegrityChecker(
              environment: env, expectedSigningDigest: release)
          .check();
      expect(result.reasons, IntegrityReason.values.toSet());
    });

    test('a detector that throws counts as a hit', () async {
      final result = await DeviceIntegrityChecker(
        environment: healthyAndroid(),
        expectedSigningDigest: release,
        detectors: [_Throws()],
      ).check();
      expect(result.compromised, isTrue);
    });

    test('the result names no detector', () async {
      final result = await DeviceIntegrityChecker(
              environment: healthyAndroid()..files.add('/sbin/.magisk'),
              expectedSigningDigest: release)
          .check();
      final text = result.toString().toLowerCase();
      for (final word in ['magisk', 'root', 'frida', 'xposed', 'debugger']) {
        expect(text, isNot(contains(word)), reason: word);
      }
    });
  });

  group('the guard', () {
    test('keeps an answer for a short time, and asks again when forced',
        () async {
      var now = DateTime.utc(2026, 11, 2, 10);
      final checker = _Counting(const IntegrityResult.healthy());
      final guard = IntegrityGuard(checker, clock: () => now);
      await guard.current();
      await guard.current();
      expect(checker.calls, 1);
      now = now.add(const Duration(seconds: 29));
      await guard.current();
      expect(checker.calls, 1);
      now = now.add(const Duration(seconds: 2));
      await guard.current();
      expect(checker.calls, 2, reason: 'expired');
      await guard.current(force: true);
      expect(checker.calls, 3);
    });

    test('callers that arrive during a check share it', () async {
      final checker = _Counting(const IntegrityResult.healthy());
      final guard = IntegrityGuard(checker);
      await Future.wait([guard.current(), guard.current(), guard.current()]);
      expect(checker.calls, 1);
    });

    test('a checker that fails is a compromised answer, not a healthy one',
        () async {
      final guard = IntegrityGuard(_Failing());
      expect((await guard.current()).compromised, isTrue);
    });
  });
}

class _Throws extends IntegrityDetector {
  @override
  IntegrityReason get reason => IntegrityReason.deviceModified;

  @override
  Future<bool> triggered(IntegrityEnvironment env) async =>
      throw StateError('unreadable');
}

class _Counting implements IntegrityChecker {
  _Counting(this.result);
  final IntegrityResult result;
  int calls = 0;

  @override
  Future<IntegrityResult> check() async {
    calls++;
    await Future<void>.delayed(Duration.zero);
    return result;
  }
}

class _Failing implements IntegrityChecker {
  @override
  Future<IntegrityResult> check() async => throw StateError('broken');
}
