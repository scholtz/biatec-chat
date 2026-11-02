import 'dart:io';

import 'package:biatec_authenticator/codes/add/account_input.dart';
import 'package:biatec_authenticator/codes/codes_controller.dart';
import 'package:biatec_authenticator/codes/codes_screen.dart';
import 'package:biatec_authenticator/codes/sensitive_clipboard.dart';
import 'package:biatec_authenticator/codes/server_clock.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import '../totp/vault/support.dart' show account;
import 'support.dart';

const key = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';
const link =
    'otpauth://totp/GitHub:alice%40example.com?secret=$key&issuer=GitHub';

Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 4; i++) {
    await tester.pump(const Duration(milliseconds: 20));
  }
}

/// A scanner that reads [text] as soon as it is shown.
Widget Function(BuildContext, ValueChanged<String>) scans(String text) =>
    (context, onScanned) => _Scans(text: text, onScanned: onScanned);

class _Scans extends StatefulWidget {
  const _Scans({required this.text, required this.onScanned});

  final String text;
  final ValueChanged<String> onScanned;

  @override
  State<_Scans> createState() => _ScansState();
}

class _ScansState extends State<_Scans> {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance
        .addPostFrameCallback((_) => widget.onScanned(widget.text));
  }

  @override
  Widget build(BuildContext context) => const Text('camera');
}

Future<CodesRig> open(WidgetTester tester,
    {String scanned = link, bool preload = false}) async {
  final rig = await CodesRig.create(now: DateTime.utc(2026, 11, 2, 10));
  if (preload) await rig.addAccount(account(seed: 9));
  tester.view.physicalSize = const Size(400, 1200);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(
      theme: buildTheme(Brightness.light),
      home: CodesScreen(services: rig.services, scanner: scans(scanned))));
  await settle(tester);
  return rig;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('adding an account', () {
    testWidgets('by QR code: preview, then saved and listed', (tester) async {
      final rig = await open(tester);
      await tester.tap(find.byKey(const Key('empty-scan')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('add-preview')), findsOneWidget);
      expect(find.text('GitHub'), findsWidgets);
      expect(find.text('alice@example.com'), findsWidgets);
      expect(await rig.vault.list(), isEmpty,
          reason: 'nothing saved before the user says so');
      await tester.tap(find.byKey(const Key('save-account')));
      await tester.pumpAndSettle();
      expect((await rig.vault.list()).single.issuer, 'GitHub');
      expect(find.byKey(const Key('add-preview')), findsNothing);
      expect(find.text('alice@example.com'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a QR code that is not an account says so', (tester) async {
      await open(tester, scanned: 'https://example.com/login');
      await tester.tap(find.byKey(const Key('empty-scan')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('add-problem')), findsOneWidget);
      expect(find.textContaining('not a two-factor account'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('by pasted link, with the engine\'s message for a bad one',
        (tester) async {
      final rig = await open(tester);
      await tester.tap(find.byKey(const Key('empty-link')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('link-field')),
          'otpauth://totp/a?secret=GEZDGNBV');
      await tester.tap(find.byKey(const Key('link-continue')));
      await tester.pump();
      expect(find.byKey(const Key('add-problem')), findsOneWidget);
      expect(find.textContaining('too short'), findsOneWidget);

      await tester.enterText(find.byKey(const Key('link-field')), link);
      await tester.tap(find.byKey(const Key('link-continue')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('save-account')));
      await tester.pumpAndSettle();
      expect(await rig.vault.list(), hasLength(1));
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('by typed key, ending in the same account as the link',
        (tester) async {
      final rig = await open(tester);
      await tester.tap(find.byKey(const Key('empty-key')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('key-issuer')), 'GitHub');
      await tester.enterText(
          find.byKey(const Key('key-label')), 'alice@example.com');
      await tester.enterText(find.byKey(const Key('key-secret')),
          'gezd gnbv gy3t qojq gezd gnbv gy3t qojq');
      await tester.tap(find.byKey(const Key('key-continue')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('save-account')));
      await tester.pumpAndSettle();
      final read =
          await rig.vault.readSecret((await rig.vault.list()).single.id);
      // Adding the link now is the same account, so it is refused as a copy.
      final c = CodesController(rig.services);
      addTearDown(c.dispose);
      await c.load();
      final fromLink = AccountInput.fromLink(link);
      await expectLater(c.add(fromLink), throwsA(isA<AddRefused>()));
      expect(read.issuer, 'GitHub');
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a bad typed key shows the reason and keeps the form',
        (tester) async {
      await open(tester);
      await tester.tap(find.byKey(const Key('empty-key')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('key-label')), 'a');
      await tester.enterText(find.byKey(const Key('key-secret')), '1!1');
      await tester.tap(find.byKey(const Key('key-continue')));
      await tester.pump();
      expect(find.byKey(const Key('add-problem')), findsOneWidget);
      expect(find.byKey(const Key('key-secret')), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a copy of an account in the list is refused with words',
        (tester) async {
      final rig = await open(tester, preload: true);
      expect(rig.vault, isNotNull);
      await tester.tap(find.byKey(const Key('add-account')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('add-link')));
      await tester.pumpAndSettle();
      // The same secret as the account already there (seed 9).
      final dup = account(seed: 9);
      final b32 = Base32.encode(dup.copySecret());
      await tester.enterText(find.byKey(const Key('link-field')),
          'otpauth://totp/Other:x?secret=$b32&issuer=Other');
      await tester.tap(find.byKey(const Key('link-continue')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('save-account')));
      await tester.pumpAndSettle();
      expect(
          find.textContaining('You already have this account'), findsOneWidget);
      expect(await rig.vault.list(), hasLength(1));
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('a refused fingerprint saves nothing and says so',
        (tester) async {
      final rig = await open(tester);
      rig.keys.denyAuth = true;
      await tester.tap(find.byKey(const Key('empty-link')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('link-field')), link);
      await tester.tap(find.byKey(const Key('link-continue')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('save-account')));
      await tester.pumpAndSettle();
      expect(find.textContaining('Nothing was saved'), findsOneWidget);
      expect(await rig.vault.list(), isEmpty);
      await tester.pumpWidget(const SizedBox());
    });
  });

  group('the sensitive clipboard', () {
    const native = MethodChannel('io.biatec.authenticator/clipboard');
    String? held;
    final calls = <MethodCall>[];

    setUp(() {
      held = null;
      calls.clear();
      final m =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      m.setMockMethodCallHandler(native, (c) async {
        calls.add(c);
        held = (c.arguments as Map)['text'] as String;
        return null;
      });
      m.setMockMethodCallHandler(SystemChannels.platform, (c) async {
        if (c.method == 'Clipboard.getData') {
          return held == null ? null : {'text': held};
        }
        if (c.method == 'Clipboard.setData') {
          held = (c.arguments as Map)['text'] as String;
        }
        return null;
      });
    });

    tearDown(() {
      final m =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      m.setMockMethodCallHandler(native, null);
      m.setMockMethodCallHandler(SystemChannels.platform, null);
    });

    testWidgets(
        'copies through the native sensitive call and clears after 30 s',
        (tester) async {
      final clipboard = PlatformSensitiveClipboard();
      await clipboard.copy('123456');
      expect(calls.single.method, 'copy');
      expect((calls.single.arguments as Map)['seconds'], 30);
      await tester.pump(const Duration(seconds: 29));
      expect(held, '123456');
      await tester.pump(const Duration(seconds: 2));
      expect(held, '', reason: 'still the code after 30 s: cleared');
    });

    testWidgets('does not clear something the user copied since',
        (tester) async {
      final clipboard = PlatformSensitiveClipboard();
      await clipboard.copy('123456');
      held = 'my shopping list';
      await tester.pump(const Duration(seconds: 31));
      expect(held, 'my shopping list');
    });

    testWidgets('a second copy starts the 30 seconds again', (tester) async {
      final clipboard = PlatformSensitiveClipboard();
      await clipboard.copy('111111');
      await tester.pump(const Duration(seconds: 20));
      await clipboard.copy('222222');
      await tester.pump(const Duration(seconds: 20));
      expect(held, '222222', reason: 'the first timer was replaced');
      await tester.pump(const Duration(seconds: 11));
      expect(held, '');
    });
  });

  group('the server clock', () {
    test('reads the Date header against the middle of the call', () {
      final c = ServerClock();
      final sent = DateTime.utc(2026, 11, 2, 10, 0, 0, 0);
      final got = DateTime.utc(2026, 11, 2, 10, 0, 0, 400);
      c.observe('Mon, 02 Nov 2026 10:00:00 GMT', sent, got);
      expect(c.offset!.inMilliseconds.abs(), lessThan(1000));
      expect(c.isOff, isFalse);
      c.observe('Mon, 02 Nov 2026 10:00:09 GMT', sent, got);
      expect(c.isOff, isTrue);
      expect(c.offset!.inSeconds, inInclusiveRange(8, 9));
    });

    test(
        'exactly at the limit is not off; a missing or broken header is ignored',
        () {
      final c = ServerClock();
      final t = DateTime.utc(2026, 11, 2, 10);
      c.observe(null, t, t);
      c.observe('not a date', t, t);
      expect(c.offset, isNull);
      expect(c.isOff, isFalse);
      c.observe('Mon, 02 Nov 2026 10:00:04 GMT', t, t);
      expect(c.isOff, isFalse);
    });

    test('listeners hear when the verdict changes, even by less than a second',
        () {
      final c = ServerClock();
      var heard = 0;
      c.addListener(() => heard++);
      final t = DateTime.utc(2026, 11, 2, 10);
      c.observe('Mon, 02 Nov 2026 10:00:05 GMT', t, t); // 5.5 s: off
      expect(c.isOff, isTrue);
      expect(heard, 1);
      c.observe('Mon, 02 Nov 2026 10:00:04 GMT', t, t); // 4.5 s: not off
      expect(c.isOff, isFalse);
      expect(heard, 2);
      c.observe('Mon, 02 Nov 2026 10:00:04 GMT', t, t);
      expect(heard, 2, reason: 'the same answer again is quiet');
    });

    test('a phone ahead is reported as negative', () {
      final c = ServerClock();
      final t = DateTime.utc(2026, 11, 2, 10, 5);
      c.observe('Mon, 02 Nov 2026 10:00:00 GMT', t, t);
      expect(c.offset!.isNegative, isTrue);
      expect(c.isOff, isTrue);
    });

    test('the client wrapper feeds it from every answer', () async {
      var now = DateTime.utc(2026, 11, 2, 10);
      final c = ServerClock(clock: () => now);
      final inner = MockClient((r) async {
        now = now.add(const Duration(milliseconds: 100));
        return http.Response('{}', 200,
            headers: {'date': 'Mon, 02 Nov 2026 10:00:30 GMT'});
      });
      final client = c.wrap(inner);
      await client.get(Uri.parse('https://oidc.example/x'));
      expect(c.isOff, isTrue);
      client.close();
    });
  });

  group('platform wiring', () {
    String read(String p) => File(p).readAsStringSync();

    test('the camera is declared as optional hardware and explained', () {
      final manifest = read('android/app/src/main/AndroidManifest.xml');
      expect(manifest, contains('android.permission.CAMERA'));
      expect(
          manifest,
          contains(
              'android:name="android.hardware.camera" android:required="false"'));
      expect(
          read('ios/Runner/Info.plist'), contains('NSCameraUsageDescription'));
      expect(read('pubspec.yaml'), contains('mobile_scanner'));
    });

    test('only the scanner screen touches the camera, and nothing is stored',
        () {
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final text = f.readAsStringSync();
        if (f.path
            .replaceAll('\\', '/')
            .endsWith('codes/add/qr_scan_screen.dart')) {
          continue;
        }
        expect(text, isNot(contains('mobile_scanner')), reason: f.path);
        expect(text, isNot(contains('image_picker')), reason: f.path);
      }
      final scan = read('lib/codes/add/qr_scan_screen.dart');
      expect(scan, isNot(contains('writeAsBytes')));
      expect(scan, isNot(contains('captureImage')));
    });

    test('copied codes are marked sensitive and expire on both platforms', () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/ClipboardChannel.kt');
      expect(kt, contains('EXTRA_IS_SENSITIVE'));
      expect(kt, contains('android.content.extra.IS_SENSITIVE'));
      expect(kt, contains('postDelayed'),
          reason: 'cleared natively too, not only by a Dart timer');
      expect(kt, contains('clearPrimaryClip'));
      final sw = read('ios/Runner/ClipboardChannel.swift');
      expect(sw, contains('.localOnly'));
      expect(sw, contains('.expirationDate'));
      expect(
          read(
              'android/app/src/main/kotlin/io/biatec/authenticator/MainActivity.kt'),
          contains('ClipboardChannel('));
      expect(read('ios/Runner/AppDelegate.swift'),
          contains('ClipboardChannel.register('));
      final project = read('ios/Runner.xcodeproj/project.pbxproj');
      expect('ClipboardChannel.swift in Sources */,'.allMatches(project).length,
          1);
    });

    test(
        'the secret key accepts a short biometric window, not none and not long',
        () {
      final kt = read(
          'android/app/src/main/kotlin/io/biatec/authenticator/VaultPlugin.kt');
      expect(kt, contains('VAULT_AUTH_SECONDS = 30'));
      expect(
          kt, contains('setUserAuthenticationParameters(VAULT_AUTH_SECONDS'));
      expect(kt, contains('UserNotAuthenticatedException'));
      expect(read('ios/Runner/VaultPlugin.swift'),
          contains('touchIDAuthenticationAllowableReuseDuration = 30'));
      expect(read('ios/Runner/VaultPlugin.swift'),
          contains('sharedContext?.invalidate()'),
          reason: 'the authenticated context ends when the app locks');
    });

    test('the shared ticker is the only timer of the list', () {
      final tile = read('lib/codes/widgets/code_tile.dart');
      expect(tile, isNot(contains('Timer')));
      expect(tile, isNot(contains('AnimationController')));
      expect(read('lib/codes/codes_screen.dart'), contains('CodeTicker('));
    });
  });
}
