// BCHAT-008: the native posture of the committed android/ and ios/ folders is a reviewed property, so it is a test. These read the project
// files (flutter test runs from the package root); the merged release manifest, which also holds what libraries add, is reviewed on release.
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

String read(String path) {
  final f = File(path);
  if (!f.existsSync()) throw StateError('$path is missing');
  return f.readAsStringSync();
}

void main() {
  group('Android', () {
    final manifest = read('android/app/src/main/AndroidManifest.xml');
    final debugManifest = read('android/app/src/debug/AndroidManifest.xml');
    final profileManifest = read('android/app/src/profile/AndroidManifest.xml');
    final gradle = read('android/app/build.gradle.kts');
    final activity = read(
        'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt');

    test('nothing is backed up or transferred', () {
      expect(manifest, contains('android:allowBackup="false"'));
      expect(manifest, contains('android:fullBackupContent="false"'));
      expect(manifest, contains('android:dataExtractionRules='));
      final rules =
          read('android/app/src/main/res/xml/data_extraction_rules.xml');
      expect(rules, contains('<cloud-backup>'));
      expect(rules, contains('<device-transfer>'));
      expect(RegExp('<include').hasMatch(rules), isFalse);
      for (final domain in [
        'root',
        'file',
        'database',
        'sharedpref',
        'external',
        'device_root',
        'device_file',
        'device_database',
        'device_sharedpref'
      ]) {
        expect('<exclude domain="$domain" />'.allMatches(rules).length, 2,
            reason: '$domain in cloud-backup and device-transfer');
      }
    });

    test('cleartext is forbidden except in the debug manifest', () {
      expect(manifest, contains('android:usesCleartextTraffic="false"'));
      expect(manifest, isNot(contains('usesCleartextTraffic="true"')));
      expect(debugManifest, contains('android:usesCleartextTraffic="true"'));
      expect(profileManifest, isNot(contains('usesCleartextTraffic="true"')));
    });

    test(
        'the launcher activity is the only component, and it is the only exported one',
        () {
      expect('android:exported="true"'.allMatches(manifest).length, 1);
      for (final tag in ['<service', '<receiver', '<provider']) {
        expect(manifest, isNot(contains(tag)), reason: '$tag in the manifest');
      }
      expect('<activity'.allMatches(manifest).length, 1);
      expect(manifest, contains('android:name=".MainActivity"'));
      expect(manifest, isNot(contains('android:debuggable')));
    });

    test('the app declares the permission it needs itself', () {
      expect(manifest, contains('android.permission.INTERNET'));
    });

    test('the window is secure and the keystore plugin is wired', () {
      expect(activity, contains('FLAG_SECURE'));
      expect(activity, contains('flutterEngine.plugins.add(KeystorePlugin('));
      expect(activity, contains('FlutterFragmentActivity'),
          reason: 'local_auth needs a FragmentActivity');
      final plugin = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/KeystorePlugin.kt');
      expect(plugin, contains('io.biatec.authenticator/keystore'));
      expect(Directory('native').existsSync(), isFalse,
          reason: 'there must be one copy of the plugins');
    });

    test('the release build is signed with a key that is not in the repository',
        () {
      expect(gradle, isNot(contains('getByName("debug")')));
      expect(gradle, contains('key.properties'));
      expect(gradle, contains('signingConfigs.getByName("release")'));
      expect(gradle, contains('isMinifyEnabled = true'));
      expect(gradle, contains('applicationId = "io.biatec.authenticator"'));
      final ignore = read('android/.gitignore');
      for (final pattern in ['key.properties', '*.jks', '*.keystore']) {
        expect(ignore, contains(pattern));
      }
    });
  });

  // CI sets this after building the release APK, so a manifest that is not found fails the test instead of skipping it.
  final requireMergedManifest =
      Platform.environment['REQUIRE_MERGED_MANIFEST'] == '1';

  group('Android merged release manifest (after flutter build apk --release)',
      () {
    // AGP writes the merged release manifest under merged_manifests or merged_manifest, depending on its version (packaged_manifests is a
    // different, later artifact and is not read). Within the first layout found, the newest file wins, so a manifest left over from an
    // older build is not read in place of the current one.
    File? newestManifest(String name) {
      final dir = Directory('build/app/intermediates/$name/release');
      if (!dir.existsSync()) return null;
      final files = dir
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('AndroidManifest.xml'))
          .toList()
        ..sort((a, b) => b.lastModifiedSync().compareTo(a.lastModifiedSync()));
      return files.firstOrNull;
    }

    final file =
        newestManifest('merged_manifests') ?? newestManifest('merged_manifest');

    test('every exported component but the launcher is guarded by a permission',
        () {
      expect(file, isNotNull,
          reason: 'build the release APK first (flutter build apk --release)');
      final xml = file!.readAsStringSync();
      expect(xml, contains('android:allowBackup="false"'));
      expect(xml, contains('android:usesCleartextTraffic="false"'));
      expect(xml, isNot(contains('android:debuggable="true"')));
      final tags = RegExp(r'<(activity|service|receiver|provider)\b[^>]*>')
          .allMatches(xml)
          .map((m) => m.group(0)!)
          .where((t) => t.contains('android:exported="true"'));
      expect(tags, isNotEmpty);
      for (final tag in tags) {
        if (tag.contains('.MainActivity')) continue;
        expect(tag, contains('android:permission='),
            reason: 'an exported component without a permission: $tag');
      }
    },
        skip: file == null && !requireMergedManifest
            ? 'no merged release manifest: build the release APK first'
            : false);
  });

  group('iOS', () {
    final plist = read('ios/Runner/Info.plist');
    final delegate = read('ios/Runner/AppDelegate.swift');
    final project = read('ios/Runner.xcodeproj/project.pbxproj');

    test(
        'App Transport Security is left at its default and there are no URL schemes',
        () {
      expect(plist, isNot(contains('NSAppTransportSecurity')));
      expect(plist, isNot(contains('NSAllowsArbitraryLoads')));
      expect(plist, isNot(contains('CFBundleURLTypes')));
      expect(plist, contains('NSFaceIDUsageDescription'));
    });

    test(
        'the Secure Enclave plugin and the screen cover are in the target and started',
        () {
      expect(delegate, contains('KeystorePlugin.register('));
      expect(delegate, contains('ScreenCover.shared.start()'));
      for (final file in ['KeystorePlugin.swift', 'ScreenCover.swift']) {
        expect('$file in Sources */,'.allMatches(project).length, 1,
            reason: '$file must be in the Runner sources build phase once');
        expect(File('ios/Runner/$file').existsSync(), isTrue);
      }
      expect(read('ios/Runner/KeystorePlugin.swift'),
          contains('io.biatec.authenticator/keystore'));
    });

    test('the bundle identifier matches Android', () {
      expect(project,
          contains('PRODUCT_BUNDLE_IDENTIFIER = io.biatec.authenticator;'));
      expect(project, isNot(contains('biatecAuthenticator')));
    });
  });

  // T-227: window and build hardening. Like the groups above, these read the committed project files.
  group('Android window and build hardening (T-227)', () {
    final activity = read(
        'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt');
    final manifest = read('android/app/src/main/AndroidManifest.xml');
    final gradle = read('android/app/build.gradle.kts');
    final proguard = read('android/app/proguard-rules.pro');

    test('the window is secure before the first frame, not after', () {
      final secure = activity.indexOf('window.setFlags(');
      final create = activity.indexOf('super.onCreate(');
      expect(secure, greaterThan(-1));
      expect(create, greaterThan(-1));
      expect(secure, lessThan(create),
          reason: 'FLAG_SECURE must be set before the view is created');
    });

    test('recents screenshots are off and overlays are hidden or ignored', () {
      expect(activity, contains('setRecentsScreenshotEnabled(false)'));
      expect(activity, contains('setHideOverlayWindows(true)'));
      expect(activity, contains('filterTouchesWhenObscured = true'));
      // The filter is only for Android 11 and older, where overlays cannot be hidden.
      expect(activity, contains('SDK_INT < 31'));
      // The overlay API needs its permission, and each call is behind its API level.
      expect(manifest, contains('android.permission.HIDE_OVERLAY_WINDOWS'));
      expect(activity, contains('Build.VERSION.SDK_INT >= 33'));
      expect(activity, contains('Build.VERSION.SDK_INT >= 31'));
    });

    test('the touch filter is set after the window exists', () {
      expect(activity.indexOf('window.decorView.filterTouchesWhenObscured'),
          greaterThan(activity.indexOf('super.onCreate(')));
    });

    test('the release build is not debuggable and strips logging', () {
      expect(gradle, contains('isDebuggable = false'));
      expect(gradle, contains('isJniDebuggable = false'));
      expect(gradle, contains('isShrinkResources = true'));
      expect(proguard, contains('-assumenosideeffects class android.util.Log'));
      expect(proguard, contains('isLoggable(...) return false'));
      for (final level in ['v', 'd', 'i', 'w', 'e', 'wtf', 'println']) {
        expect(proguard, contains('public static int $level(...);'),
            reason: 'Log.$level is stripped');
      }
    });

    test('no source file logs', () {
      final dir = Directory('android/app/src/main/kotlin');
      for (final f in dir
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.kt'))) {
        final code = f.readAsStringSync();
        for (final pattern in [
          r'\bLog\.[a-zA-Z]+\(',
          r'printStackTrace',
          r'System\.(out|err)',
          r'\bprintln\(',
          r'Timber\.',
        ]) {
          expect(RegExp(pattern).hasMatch(code), isFalse,
              reason: '${f.path} must not log ($pattern)');
        }
      }
    });

    test('no Dart source in lib logs', () {
      // R8 cannot strip Dart print/debugPrint (they reach logcat through the engine), so the Dart code must not log at all.
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final code = f.readAsStringSync();
        for (final pattern in [
          r'(^|[^\w.])print\(',
          r'\bdebugPrint\(',
          r'\bdeveloper\.log\(',
          r"import 'dart:developer'",
        ]) {
          expect(RegExp(pattern, multiLine: true).hasMatch(code), isFalse,
              reason: '${f.path} must not log ($pattern)');
        }
      }
    });

    test(
        'the data extraction rules still exclude every domain from both transfers',
        () {
      final rules =
          read('android/app/src/main/res/xml/data_extraction_rules.xml');
      expect('<exclude domain='.allMatches(rules).length, 18);
    });
  });

  group('iOS storage, keyboard and privacy (T-227)', () {
    final delegate = read('ios/Runner/AppDelegate.swift');
    final project = read('ios/Runner.xcodeproj/project.pbxproj');
    final privacy = read('ios/Runner/PrivacyInfo.xcprivacy');

    test('app folders are excluded from backup at launch', () {
      expect(delegate, contains('excludeStorageFromBackup()'));
      expect(delegate, contains('isExcludedFromBackup = true'));
      // Library holds Application Support and Caches, so it and Documents cover every app folder.
      for (final place in ['.libraryDirectory', '.documentDirectory']) {
        expect(delegate, contains(place));
      }
      // A failure is not hidden by try?; it is retried on the next launch, and only a debug build says so.
      expect(delegate, isNot(contains('try? url.setResourceValues')));
      expect(delegate, contains('could not exclude'));
      // Called inside didFinishLaunching, before the app (and its plugins) start to write.
      final launch = delegate.indexOf('didFinishLaunchingWithOptions');
      final exclude =
          delegate.indexOf('AppDelegate.excludeStorageFromBackup()');
      final superCall =
          delegate.indexOf('return super.application(application, didFinish');
      expect(exclude, greaterThan(launch));
      expect(exclude, lessThan(superCall),
          reason: 'before super starts the Flutter engine and plugins');
    });

    test('third-party keyboards are not allowed', () {
      expect(delegate, contains('shouldAllowExtensionPointIdentifier'));
      expect(delegate, contains('extensionPointIdentifier != .keyboard'));
      // FlutterAppDelegate does not implement this method, so "override" would not compile; @objc exposes it to UIKit.
      expect(
          RegExp(r'override\s+func\s+application\(\s*_ application: UIApplication,\s*shouldAllowExtensionPointIdentifier')
              .hasMatch(delegate),
          isFalse);
      expect(
          RegExp(r'@objc\s+func\s+application\(\s*_ application: UIApplication,\s*shouldAllowExtensionPointIdentifier')
              .hasMatch(delegate),
          isTrue);
    });

    test('no Swift source logs outside a debug build', () {
      for (final f in Directory('ios/Runner')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.swift'))) {
        // Drop every #if DEBUG ... #endif block, then nothing may log.
        final code = f
            .readAsStringSync()
            .replaceAll(RegExp(r'#if DEBUG[\s\S]*?#endif'), '');
        for (final pattern in [
          r'\bNSLog\(',
          r'\bprint\(',
          r'\bos_log\(',
          r'Logger\('
        ]) {
          expect(RegExp(pattern).hasMatch(code), isFalse,
              reason: '${f.path} must not log in a release build ($pattern)');
        }
      }
    });

    test('the privacy manifest says no tracking, and is part of the app bundle',
        () {
      // Whitespace-tolerant: a checkout may have CRLF line endings.
      expect(
          RegExp(r'<key>NSPrivacyTracking</key>\s*<false/>').hasMatch(privacy),
          isTrue);
      expect(
          RegExp(r'<key>NSPrivacyTrackingDomains</key>\s*<array/>')
              .hasMatch(privacy),
          isTrue);
      expect(privacy, contains('NSPrivacyCollectedDataTypeDeviceID'));
      // The device token, key and signed approvals are tied to the user's account.
      expect(privacy, contains('NSPrivacyCollectedDataTypeUserID'));
      expect(
          'PrivacyInfo.xcprivacy in Resources */,'.allMatches(project).length,
          1,
          reason: 'it must be in the Resources build phase once');
      expect('path = PrivacyInfo.xcprivacy;'.allMatches(project).length, 1);
    });

    test('the keychain items stay on this device', () {
      final storage = read('lib/security/secure_storage.dart');
      expect(storage, contains('_this_device'));
    });
  });

  group('release build in CI (T-227)', () {
    final ci = File('../../.github/workflows/ci.yml');

    test('is obfuscated, splits the symbols off and keeps them as an artifact',
        () {
      final text = ci.readAsStringSync();
      expect(text, contains('flutter build apk --release --obfuscate'));
      expect(text, contains('--split-debug-info=build/symbols'));
      expect(text, contains('actions/upload-artifact@'));
      expect(text, contains('authenticator-debug-symbols'));
      // The repository is public, so only the encrypted symbols are uploaded, never the folder itself.
      expect(text,
          contains('path: apps/biatec_authenticator/build/symbols.tar.gz.gpg'));
      expect(text,
          isNot(contains('path: apps/biatec_authenticator/build/symbols\n')));
      expect(text, contains('--symmetric --cipher-algo AES256'));
      // The throwaway signing key is removed again and never a repository file.
      expect(text,
          contains('rm -f android/key.properties android/ci-throwaway.jks'));
      expect(text, contains('flutter test test/platform_hardening_test.dart'));
    }, skip: ci.existsSync() ? false : 'run from the repository checkout');
  });
}
