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

  group('Android merged release manifest (after flutter build apk --release)',
      () {
    final dir = Directory('build/app/intermediates/merged_manifests/release');
    final file = dir.existsSync()
        ? dir
            .listSync(recursive: true)
            .whereType<File>()
            .where((f) => f.path.endsWith('AndroidManifest.xml'))
            .firstOrNull
        : null;

    test('every exported component but the launcher is guarded by a permission',
        () {
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
        skip: file == null
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
      final secure = activity.indexOf('FLAG_SECURE');
      final create = activity.indexOf('super.onCreate(');
      expect(secure, greaterThan(-1));
      expect(create, greaterThan(-1));
      expect(secure, lessThan(create),
          reason: 'FLAG_SECURE must be set before the view is created');
      expect(activity, contains('setFlags('));
    });

    test('recents screenshots are off and overlays are hidden or ignored', () {
      expect(activity, contains('setRecentsScreenshotEnabled(false)'));
      expect(activity, contains('setHideOverlayWindows(true)'));
      expect(activity, contains('filterTouchesWhenObscured = true'));
      // The overlay API needs its permission, and each call is behind its API level.
      expect(manifest, contains('android.permission.HIDE_OVERLAY_WINDOWS'));
      expect(activity, contains('Build.VERSION.SDK_INT >= 33'));
      expect(activity, contains('Build.VERSION.SDK_INT >= 31'));
    });

    test('the touch filter is set after the window exists', () {
      expect(activity.indexOf('filterTouchesWhenObscured'),
          greaterThan(activity.indexOf('super.onCreate(')));
    });

    test('the release build is not debuggable and strips logging', () {
      expect(gradle, contains('isDebuggable = false'));
      expect(gradle, contains('isJniDebuggable = false'));
      expect(gradle, contains('isShrinkResources = true'));
      expect(proguard, contains('-assumenosideeffects class android.util.Log'));
      for (final level in ['v', 'd', 'i', 'w', 'e']) {
        expect(proguard, contains('public static int $level(...);'),
            reason: 'Log.$level is stripped');
      }
      expect(proguard, contains('-repackageclasses'));
    });

    test('no source file logs', () {
      final dir = Directory('android/app/src/main/kotlin');
      for (final f in dir
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.kt'))) {
        expect(
            RegExp(r'Log\.[vdiwe]\(').hasMatch(f.readAsStringSync()), isFalse,
            reason: '${f.path} must not log');
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
      for (final place in [
        '.applicationSupportDirectory',
        '.documentDirectory',
        '.libraryDirectory'
      ]) {
        expect(delegate, contains(place));
      }
      // Called from didFinishLaunching, before anything is written.
      final launch = delegate.indexOf('didFinishLaunchingWithOptions');
      expect(delegate.indexOf('AppDelegate.excludeStorageFromBackup()'),
          greaterThan(launch));
    });

    test('third-party keyboards are not allowed', () {
      expect(delegate, contains('shouldAllowExtensionPointIdentifier'));
      expect(delegate, contains('extensionPointIdentifier != .keyboard'));
    });

    test('the privacy manifest says no tracking, and is part of the app bundle',
        () {
      expect(privacy, contains('<key>NSPrivacyTracking</key>\n\t<false/>'));
      expect(
          privacy, contains('<key>NSPrivacyTrackingDomains</key>\n\t<array/>'));
      expect(privacy, contains('NSPrivacyCollectedDataTypeDeviceID'));
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
      // The throwaway signing key is removed again and never a repository file.
      expect(text,
          contains('rm -f android/key.properties android/ci-throwaway.jks'));
      expect(text, contains('flutter test test/platform_hardening_test.dart'));
    }, skip: ci.existsSync() ? false : 'run from the repository checkout');
  });
}
