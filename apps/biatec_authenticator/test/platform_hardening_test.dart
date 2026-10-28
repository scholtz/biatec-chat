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
      // No attribute switches cleartext on for every host: the debug build's own network config allows the developer's machine only.
      expect(debugManifest, isNot(contains('usesCleartextTraffic="true"')));
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
        'App Transport Security has no exceptions and there are no URL schemes',
        () {
      expect(plist, isNot(contains('NSAllowsArbitraryLoads')));
      expect(plist, isNot(contains('NSExceptionDomains')));
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
}
