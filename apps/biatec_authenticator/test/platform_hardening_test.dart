// BCHAT-008: the native posture of the committed android/ and ios/ folders is a reviewed property, so it is a test. These read the project
// files (flutter test runs from the package root); the merged release manifest, which also holds what libraries add, is checked in CI after the release build.
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// Every data domain Android can back up or transfer; the extraction rules must exclude each one.
const excludedDomains = [
  'root',
  'file',
  'database',
  'sharedpref',
  'external',
  'device_root',
  'device_file',
  'device_database',
  'device_sharedpref',
];

String read(String path) {
  final f = File(path);
  if (!f.existsSync()) throw StateError('$path is missing');
  return f.readAsStringSync();
}

/// Whether a `#if`/`#elseif` condition holds only in a debug build (`DEBUG`, `DEBUG && X`), only in a release build (`!DEBUG`), or neither.
enum _Build { debug, release, either }

_Build _branchOf(String condition) {
  // A trailing // comment is not part of the condition.
  final c = condition.split('//').first.replaceAll(RegExp(r'\s+'), '');
  // With an "||" the branch can also hold in a release build.
  if (c.contains('||')) return _Build.either;
  if (c == 'DEBUG' || c.startsWith('DEBUG&&') || c.endsWith('&&DEBUG')) {
    return _Build.debug;
  }
  if (c == '!DEBUG') return _Build.release;
  return _Build.either;
}

/// The Swift lines a release build compiles. A branch that holds only in a debug build is dropped: `#if DEBUG`, `#elseif DEBUG`, and the
/// `#else` of `#if !DEBUG`. Everything else is kept (the `#else` of `#if DEBUG`, any other condition), and nested blocks are followed.
String releaseOnly(String swift) {
  // Per open #if: what its first condition was, and what the branch now being read is.
  final open = <({_Build first, _Build now})>[];
  final kept = <String>[];
  for (final line in swift.split('\n')) {
    final t = line.trim();
    if (t.startsWith('#if')) {
      final b = _branchOf(t.substring(3));
      open.add((first: b, now: b));
    } else if (t.startsWith('#elseif')) {
      if (open.isNotEmpty) {
        open.last = (first: open.last.first, now: _branchOf(t.substring(7)));
      }
    } else if (t.startsWith('#else')) {
      if (open.isNotEmpty) {
        final first = open.last.first;
        open.last = (
          first: first,
          now: switch (first) {
            _Build.debug => _Build.release,
            _Build.release => _Build.debug,
            _Build.either => _Build.either,
          }
        );
      }
    } else if (t.startsWith('#endif')) {
      if (open.isNotEmpty) open.removeLast();
    } else if (!open.any((o) => o.now == _Build.debug)) {
      kept.add(line);
    }
  }
  return kept.join('\n');
}

void main() {
  test('releaseOnly keeps #else branches and follows nested blocks', () {
    const swift = '#if DEBUG\nNSLog(a)\n#if os(iOS)\nx\n#endif\nNSLog(b)\n'
        '#else\nNSLog(c)\n#endif\nrelease()';
    final kept = releaseOnly(swift);
    expect(kept, isNot(contains('NSLog(a)')));
    expect(kept, isNot(contains('NSLog(b)')));
    expect(kept, contains('NSLog(c)'));
    expect(kept, contains('release()'));
    // The #else of !DEBUG and an #elseif DEBUG are debug-only; a compound DEBUG condition is too.
    final other = releaseOnly('#if !DEBUG\nrel()\n#else\nNSLog(d)\n#endif\n'
        '#if os(iOS)\nios()\n#elseif DEBUG\nNSLog(e)\n#endif\n'
        '#if DEBUG && targetEnvironment(simulator)\nNSLog(f)\n#endif');
    expect(other, contains('rel()'));
    expect(other, contains('ios()'));
    expect(other, isNot(contains('NSLog(d)')));
    expect(other, isNot(contains('NSLog(e)')));
    expect(other, isNot(contains('NSLog(f)')));
    // An "||" can hold in release, so that branch is kept.
    expect(releaseOnly('#if DEBUG && X || os(macOS)\nNSLog(g)\n#endif'),
        contains('NSLog(g)'));
    // A trailing comment does not change the condition.
    expect(releaseOnly('#if DEBUG // diagnostics\nNSLog(h)\n#endif'),
        isNot(contains('NSLog(h)')));
  });

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
      for (final domain in excludedDomains) {
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

  // CI sets this after building the release APK, so a manifest that is not found fails the test instead of skipping it.
  final requireMergedManifest =
      Platform.environment['REQUIRE_MERGED_MANIFEST'] == '1';

  group('Android merged release manifest (after flutter build apk --release)',
      () {
    // The final merged release manifest, by the path of the task that writes it, so neither recency nor an intermediate (main-only)
    // manifest decides: processReleaseManifest (current AGP), then the older layout without a task folder.
    final file = [
      'build/app/intermediates/merged_manifests/release/processReleaseManifest/AndroidManifest.xml',
      'build/app/intermediates/merged_manifests/release/AndroidManifest.xml',
    ].map(File.new).where((f) => f.existsSync()).firstOrNull;

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

    test('touches are not gated window-wide on partial occlusion', () {
      // Gating every partially obscured touch locks the whole app on Android 10 and 11 whenever a chat head, a screen filter or even the
      // app's own toast is on screen; the partial-occlusion gap is documented and left to a check on the approve action.
      expect(activity, isNot(contains('override fun dispatchTouchEvent(')));
      expect(activity, contains('Known gap on Android 10 and 11'));
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
      // Kotlin and Java, wherever they sit under src/main. Flutter's generated GeneratedPluginRegistrant is not ours (it is rewritten on
      // every build, and its Log calls are stripped by R8 with the rest).
      for (final f in Directory('android/app/src/main')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.kt') || f.path.endsWith('.java'))
          .where((f) => !f.path.endsWith('GeneratedPluginRegistrant.java'))) {
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
          r'\bdebugPrintStack\(',
          r'\bdeveloper\.log\(',
          r'''import\s+['"]dart:developer['"]''',
          r'\bstd(out|err)\.',
          r'''import\s+['"]package:(logging|logger)/''',
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
      // Each section on its own excludes every domain, so a domain dropped from one transfer fails even if it is listed twice in the other.
      for (final section in ['cloud-backup', 'device-transfer']) {
        final body = RegExp('<$section>([\\s\\S]*?)</$section>')
            .firstMatch(rules)
            ?.group(1);
        expect(body, isNotNull, reason: '<$section> is missing');
        for (final domain in excludedDomains) {
          expect(body, contains('<exclude domain="$domain" />'),
              reason: '$domain in $section');
        }
      }
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
      for (final place in [
        '.libraryDirectory',
        '.applicationSupportDirectory',
        '.documentDirectory'
      ]) {
        expect(delegate, contains(place));
      }
      // A failure is not hidden by try?; it is retried on the next launch, and only a debug build says so.
      expect(delegate, isNot(contains('try? url.setResourceValues')));
      // Documents is created before it is flagged, so a folder created later in the session is covered from the start.
      expect(
          delegate,
          contains(
              'fm.createDirectory(at: url, withIntermediateDirectories: true)'));
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
        final code = releaseOnly(f.readAsStringSync());
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
      // The code itself, not the doc comment that names the same class.
      expect(
          RegExp(r'^[^/\n]*iOptions:\s*IOSOptions\(\s*accessibility:\s*KeychainAccessibility\.unlocked_this_device\s*\)',
                  multiLine: true)
              .hasMatch(storage),
          isTrue);
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
      // Every uploaded path that names the symbols is the encrypted file (whatever the line endings or a trailing slash).
      final symbolPaths =
          RegExp(r'^\s*path:\s*(\S*symbols\S*)\s*$', multiLine: true)
              .allMatches(text)
              .map((m) => m.group(1)!)
              .toList();
      expect(symbolPaths, isNotEmpty);
      for (final p in symbolPaths) {
        expect(p, endsWith('.gpg'), reason: 'unencrypted symbols upload: $p');
      }
      // Nor on any other line, such as an entry of a multi-line "path: |" list: the folder itself is never named as a whole.
      expect(
          RegExp(r'build/symbols/?[ \t]*\r?$', multiLine: true).hasMatch(text),
          isFalse,
          reason: 'the raw symbols folder is named for upload');
      expect(text, contains('--symmetric --cipher-algo AES256'));
      // The throwaway signing key is removed again and never a repository file.
      expect(text,
          contains('rm -f android/key.properties android/ci-throwaway.jks'));
      // The unit tests run before the build, so a failing build still reports them; the hardening test runs again after it, with a
      // missing merged manifest made a failure.
      final build = text.indexOf('flutter build apk --release');
      final unit =
          text.indexOf(RegExp(r'^\s*- run: flutter test\s*$', multiLine: true));
      final hardening = text.indexOf('REQUIRE_MERGED_MANIFEST: "1"');
      expect(unit, greaterThan(-1));
      expect(unit, lessThan(build));
      expect(hardening, greaterThan(build));
      expect(text, contains('flutter test test/platform_hardening_test.dart'));
    }, skip: ci.existsSync() ? false : 'run from the repository checkout');
  });
}
