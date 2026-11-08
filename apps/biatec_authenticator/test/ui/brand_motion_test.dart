// T-231: the app's identity, motion and haptics. Native files are read as text and image headers (flutter test runs from the package root).
import 'dart:io';
import 'dart:typed_data';

import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:biatec_authenticator/lock/lock_screens.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../lock/support.dart';

Future<void> pumpApp(WidgetTester tester, Widget home,
    {bool reduced = false,
    double text = 1,
    bool dark = false,
    Size size = const Size(400, 900)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(dark ? Brightness.dark : Brightness.light),
    builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context).copyWith(disableAnimations: reduced),
        child: child!),
    home: home,
  ));
}

class Probe extends StatelessWidget {
  const Probe(this.run, {super.key});
  final void Function(BuildContext) run;

  @override
  Widget build(BuildContext context) {
    return TextButton(onPressed: () => run(context), child: const Text('go'));
  }
}

Uint8List png(String path) => File(path).readAsBytesSync();

({int w, int h, int colorType}) pngInfo(String path) {
  final b = ByteData.sublistView(png(path));
  expect(b.getUint32(0), 0x89504E47, reason: '$path is a PNG');
  return (w: b.getUint32(16), h: b.getUint32(20), colorType: b.getUint8(25));
}

void main() {
  group('haptics', () {
    testWidgets('each moment plays its own feedback', (tester) async {
      final played = <HapticKind>[];
      final haptics = Haptics(sink: (k) async => played.add(k));
      late BuildContext context;
      await pumpApp(tester, Probe((c) => context = c));
      await tester.tap(find.text('go'));
      await haptics.copy(context);
      await haptics.approve(context);
      await haptics.deny(context);
      await haptics.error(context);
      expect(played, HapticKind.values);
    });

    testWidgets('nothing plays when the system asks for reduced motion',
        (tester) async {
      final played = <HapticKind>[];
      final haptics = Haptics(sink: (k) async => played.add(k));
      late BuildContext context;
      await pumpApp(tester, Probe((c) => context = c), reduced: true);
      await tester.tap(find.text('go'));
      await haptics.copy(context);
      await haptics.approve(context);
      await haptics.deny(context);
      await haptics.error(context);
      expect(played, isEmpty);
    });

    testWidgets('a device without haptics is not an error', (tester) async {
      final haptics = Haptics(sink: (_) async => throw StateError('no motor'));
      late BuildContext context;
      await pumpApp(tester, Probe((c) => context = c));
      await tester.tap(find.text('go'));
      await haptics.copy(context);
    });

    test('Haptics is the only code that calls the platform feedback', () {
      final offenders = <String>[];
      for (final f in Directory('lib')
          .listSync(recursive: true)
          .whereType<File>()
          .where((f) => f.path.endsWith('.dart'))) {
        final path = f.path.replaceAll('\\', '/');
        if (path == 'lib/ui/haptics.dart') continue;
        if (f.readAsStringSync().contains('HapticFeedback')) {
          offenders.add(path);
        }
      }
      expect(offenders, isEmpty);
    });
  });

  group('motion', () {
    testWidgets('a page change is a short fade, and none under reduce-motion',
        (tester) async {
      Future<int> fades(bool reduced) async {
        await pumpApp(
            tester,
            Builder(
                builder: (context) => TextButton(
                    onPressed: () => Navigator.of(context).push(
                        MaterialPageRoute<void>(
                            builder: (_) =>
                                const Scaffold(body: Text('next')))),
                    child: const Text('open'))),
            reduced: reduced);
        await tester.tap(find.text('open'));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 50));
        final count = find
            .ancestor(
                of: find.text('next'), matching: find.byType(FadeTransition))
            .evaluate()
            .length;
        final moving = find
            .ancestor(
                of: find.text('next'), matching: find.byType(SlideTransition))
            .evaluate()
            .length;
        await tester.pumpAndSettle();
        // Back to the first page for the next run.
        await tester.pumpWidget(const SizedBox());
        return count + moving;
      }

      final moving = await fades(false);
      final still = await fades(true);
      expect(moving, greaterThan(still), reason: 'our fade and slide');
    });

    testWidgets('the countdown ring glides, and jumps under reduce-motion',
        (tester) async {
      Widget ring(double f) => Scaffold(
          body: Center(
              child: CountdownRing(fraction: f, seconds: 10, urgent: false)));
      double? value() => tester
          .widget<CircularProgressIndicator>(
              find.byType(CircularProgressIndicator))
          .value;

      await pumpApp(tester, ring(1));
      await tester.pumpAndSettle();
      await tester.pumpWidget(MaterialApp(home: ring(0.5)));
      await tester.pump();
      await tester.pump(Motion.ring ~/ 2);
      expect(value(), allOf(greaterThan(0.5), lessThan(1.0)),
          reason: 'part-way between the two values');
      await tester.pumpAndSettle();
      expect(value(), 0.5);

      await pumpApp(tester, ring(1), reduced: true);
      await pumpApp(tester, ring(0.25), reduced: true);
      await tester.pumpAndSettle();
      expect(value(), 0.25, reason: 'immediately');
      expect(tester.hasRunningAnimations, isFalse);
    });

    testWidgets('nothing animates on the lock screen', (tester) async {
      final lock = LockController(
          authenticator: FakeAuthenticator(), store: MemoryLockStore());
      addTearDown(lock.dispose);
      await pumpApp(tester, LockScreen(lock: lock));
      await tester.pump(const Duration(milliseconds: 400));
      expect(lock.state, AppLockState.locked);
      expect(tester.hasRunningAnimations, isFalse);
      expect(find.byType(BrandMark), findsOneWidget);
    });
  });

  group('identity', () {
    testWidgets('the mark is decoration: hidden from screen readers',
        (tester) async {
      final handle = tester.ensureSemantics();
      await pumpApp(tester, const Scaffold(body: Center(child: BrandMark())));
      expect(find.bySemanticsLabel(RegExp('.')), findsNothing);
      handle.dispose();
    });

    for (final dark in [false, true]) {
      testWidgets(
          'lock and empty screens pass the guidelines at 3x, dark=$dark',
          (tester) async {
        final handle = tester.ensureSemantics();
        final lock = LockController(
            authenticator: FakeAuthenticator(), store: MemoryLockStore());
        addTearDown(lock.dispose);
        await pumpApp(tester, LockScreen(lock: lock),
            text: 3, dark: dark, size: const Size(400, 2400));
        await expectLater(tester, meetsGuideline(textContrastGuideline));
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        await pumpApp(
            tester,
            const Scaffold(
                body: EmptyState(
                    icon: Icons.key,
                    title: 'Nothing here yet',
                    message: 'Add an account to see codes.',
                    actionLabel: 'Add',
                    onAction: null)),
            text: 3,
            dark: dark,
            size: const Size(400, 2400));
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(textContrastGuideline));
        handle.dispose();
      });
    }

    test('codes use tabular figures so they do not jitter', () {
      final tile = File('lib/codes/widgets/code_tile.dart').readAsStringSync();
      final ring = File('lib/ui/countdown_ring.dart').readAsStringSync();
      expect(tile, contains('FontFeature.tabularFigures()'));
      expect(ring, contains('FontFeature.tabularFigures()'));
    });

    test('the Dart mark and the generated assets use the same shield', () {
      final dart = File('lib/ui/brand_mark.dart').readAsStringSync();
      final script = File('tool/make_brand_assets.py').readAsStringSync();
      for (final p in ['50, 6', '88, 20', '88, 48', '12, 20']) {
        expect(dart, contains(p));
        expect(script, contains(p));
      }
      expect(dart, contains('0xFF1B5E8C'));
      expect(script, contains('0x1B, 0x5E, 0x8C'));
    });
  });

  group('native assets', () {
    const res = 'android/app/src/main/res';

    test('the Android icon is adaptive with a monochrome layer', () {
      final xml =
          File('$res/mipmap-anydpi-v26/ic_launcher.xml').readAsStringSync();
      for (final layer in ['background', 'foreground', 'monochrome']) {
        expect(xml, contains('<$layer'));
      }
      for (final f in [
        'drawable/ic_launcher_foreground.xml',
        'drawable/ic_launcher_monochrome.xml',
        'values/colors.xml',
      ]) {
        expect(File('$res/$f').existsSync(), isTrue, reason: f);
      }
      expect(File('$res/mipmap-xxxhdpi/ic_launcher.png').existsSync(), isTrue,
          reason: 'older Android');
    });

    test('the Android splash shows the mark, light and dark', () {
      final v21 =
          File('$res/drawable-v21/launch_background.xml').readAsStringSync();
      expect(v21, contains('@drawable/ic_splash_mark'));
      final light = File('$res/values/colors.xml').readAsStringSync();
      final night = File('$res/values-night/colors.xml').readAsStringSync();
      RegExp bg(String s) =>
          RegExp('name="splash_background">(#[0-9A-Fa-f]+)<');
      expect(bg(light).firstMatch(light)!.group(1),
          isNot(bg(night).firstMatch(night)!.group(1)));
      for (final f in ['values-v31', 'values-night-v31']) {
        expect(File('$res/$f/styles.xml').readAsStringSync(),
            contains('windowSplashScreenAnimatedIcon'));
      }
    });

    test('the iOS icons exist at every size, flat, with dark and tinted', () {
      const dir = 'ios/Runner/Assets.xcassets/AppIcon.appiconset';
      final json = File('$dir/Contents.json').readAsStringSync();
      final names = RegExp(r'"filename"\s*:\s*"([^"]+)"')
          .allMatches(json)
          .map((m) => m.group(1)!)
          .toSet();
      expect(
          names,
          containsAll([
            'Icon-App-1024x1024-dark.png',
            'Icon-App-1024x1024-tinted.png'
          ]));
      for (final n in names) {
        final info = pngInfo('$dir/$n');
        expect(info.w, info.h);
        expect(info.colorType, isNot(6), reason: '$n has no alpha channel');
      }
      expect(pngInfo('$dir/Icon-App-1024x1024@1x.png').w, 1024);
      expect(pngInfo('$dir/Icon-App-60x60@3x.png').w, 180);
      expect(pngInfo('$dir/Icon-App-20x20@2x.png').w, 40);
    });

    test('the iOS launch screen follows light and dark', () {
      final story = File('ios/Runner/Base.lproj/LaunchScreen.storyboard')
          .readAsStringSync();
      expect(story, contains('systemBackgroundColor'));
      expect(
          pngInfo('ios/Runner/Assets.xcassets/LaunchImage.imageset/LaunchImage@2x.png')
              .w,
          336);
    });
  });
}
