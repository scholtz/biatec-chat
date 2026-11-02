import 'dart:io';

import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:biatec_authenticator/settings/app_info.dart';
import 'package:biatec_authenticator/settings/settings_screen.dart';
import 'package:biatec_authenticator/settings/theme_controller.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

class MemoryThemeStore implements ThemePreferenceStore {
  String? value;
  bool failWrites = false;
  bool failReads = false;

  @override
  Future<String?> read() async {
    if (failReads) throw StateError('locked');
    return value;
  }

  @override
  Future<void> write(String v) async {
    if (failWrites) throw StateError('full');
    value = v;
  }
}

Future<void> _open(WidgetTester tester, Widget screen,
    {Brightness brightness = Brightness.light,
    double text = 1,
    Size size = const Size(360, 800)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester.pumpWidget(MaterialApp(
      theme: buildTheme(brightness),
      home: ThemeScope(
          controller: ThemeController(store: MemoryThemeStore()),
          child: screen)));
  await tester.pumpAndSettle();
}

Widget _screen(SettingsRig rig, {AppConfig? config}) => SettingsScreen(
    services: rig.services, config: config ?? AppConfig.resolve(''));

void main() {
  group('layout', () {
    testWidgets('three labelled groups, and every control describes itself',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      for (final title in ['Security', 'Appearance', 'About']) {
        expect(find.text(title), findsOneWidget, reason: title);
      }
      for (final key in [
        'biometric',
        'threshold',
        'block-unverified',
        'trusted-clients',
        'theme-system',
        'theme-light',
        'theme-dark',
        'about-version',
        'oidc-server',
        'about-key',
        'about-licenses',
      ]) {
        final tile = find.byKey(Key(key));
        expect(tile, findsOneWidget, reason: key);
        final widget = tester.widget(tile);
        final hasDescription = widget is ListTile
            ? widget.subtitle != null
            : widget is SwitchListTile
                ? widget.subtitle != null
                : false;
        expect(hasDescription, isTrue, reason: '$key has a description');
      }
    });

    testWidgets('trusted clients opens from the security group',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      await tester.tap(find.byKey(const Key('trusted-clients')));
      await tester.pumpAndSettle();
      expect(find.text('Trusted clients and thresholds'), findsWidgets);
      expect(find.byKey(const Key('client-biatec-chat')), findsOneWidget);
    });
  });

  group('states of the security group', () {
    testWidgets('loading', (tester) async {
      final rig = SettingsRig();
      tester.view.physicalSize = const Size(360, 2000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(home: _screen(rig)));
      expect(find.byKey(const Key('loading')), findsOneWidget);
      expect(find.text('About'), findsOneWidget,
          reason: 'About is there while loading');
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('loading')), findsNothing);
    });

    testWidgets('not available keeps Appearance and About', (tester) async {
      final rig = SettingsRig();
      final off = SettingsServices(
          client: rig.services.client,
          keys: rig.keys,
          enrollment: rig.enrollment,
          gate: rig.gate,
          enabled: false);
      await _open(
          tester, SettingsScreen(services: off, config: AppConfig.resolve('')),
          size: const Size(360, 2000));
      expect(find.byKey(const Key('not-available')), findsOneWidget);
      expect(find.byKey(const Key('theme-mode')), findsOneWidget);
      expect(find.byKey(const Key('about-version')), findsOneWidget);
    });

    testWidgets('offline says so, retries, and About stays', (tester) async {
      final oidc = FakeSettingsOidc()..offline = true;
      final rig = SettingsRig(oidc: oidc);
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      expect(find.byKey(const Key('unavailable')), findsOneWidget);
      expect(find.byKey(const Key('about-version')), findsOneWidget);
      oidc.offline = false;
      await tester.tap(find.text('Try again'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('biometric')), findsOneWidget);
    });

    for (final brightness in Brightness.values) {
      for (final phase in ['ready', 'offline']) {
        testWidgets('$phase holds at 3x text, ${brightness.name}',
            (tester) async {
          final semantics = tester.ensureSemantics();
          final oidc = FakeSettingsOidc()..offline = phase == 'offline';
          final rig = SettingsRig(oidc: oidc);
          await _open(tester, _screen(rig),
              brightness: brightness, text: 3, size: const Size(320, 6000));
          expect(tester.takeException(), isNull);
          await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
          await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
          semantics.dispose();
        });
      }
    }
  });

  group('appearance', () {
    testWidgets('the choice applies at once and is remembered', (tester) async {
      final store = MemoryThemeStore();
      final rig = SettingsRig();
      tester.view.physicalSize = const Size(400, 2400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final theme = ThemeController(store: store);
      await tester.pumpWidget(AuthenticatorApp(
        config: AppConfig.resolve(''),
        settings: rig.services,
        theme: theme,
      ));
      await tester.pumpAndSettle();
      expect(tester.widget<MaterialApp>(find.byType(MaterialApp)).themeMode,
          ThemeMode.system);

      final saving = theme.select(ThemeMode.dark);
      await tester.pump();
      expect(tester.widget<MaterialApp>(find.byType(MaterialApp)).themeMode,
          ThemeMode.dark);
      await saving;
      expect(store.value, 'dark');

      // "Restart": a new controller on the same storage.
      final next = ThemeController(store: store);
      await next.load();
      expect(next.mode, ThemeMode.dark);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('the rows say which one is chosen, by a check and by words',
        (tester) async {
      final semantics = tester.ensureSemantics();
      final rig = SettingsRig();
      final theme = ThemeController(store: MemoryThemeStore());
      tester.view.physicalSize = const Size(400, 2400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home: ThemeScope(controller: theme, child: _screen(rig))));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('theme-light')));
      await tester.pumpAndSettle();
      expect(theme.mode, ThemeMode.light);
      expect(
          find.descendant(
              of: find.byKey(const Key('theme-light')),
              matching: find.byIcon(Icons.check)),
          findsOneWidget);
      expect(
          find.descendant(
              of: find.byKey(const Key('theme-dark')),
              matching: find.byIcon(Icons.check)),
          findsNothing);
      expect(tester.getSemantics(find.byKey(const Key('theme-light'))).label,
          contains('Chosen'));
      expect(tester.getSemantics(find.byKey(const Key('theme-dark'))).label,
          isNot(contains('Chosen')));
      semantics.dispose();
    });

    test('an unknown saved value leaves the system setting', () async {
      final store = MemoryThemeStore()..value = 'purple';
      final c = ThemeController(store: store);
      await c.load();
      expect(c.mode, ThemeMode.system);
    });

    test('unreadable storage leaves the system setting', () async {
      final store = MemoryThemeStore()
        ..value = 'dark'
        ..failReads = true;
      final c = ThemeController(store: store);
      await c.load();
      expect(c.mode, ThemeMode.system);
    });

    test('a failed save still applies the choice for this run', () async {
      final store = MemoryThemeStore()..failWrites = true;
      final c = ThemeController(store: store);
      await c.select(ThemeMode.light);
      expect(c.mode, ThemeMode.light);
    });

    test('a choice made before the saved one is read wins', () async {
      final store = MemoryThemeStore()..value = 'dark';
      final c = ThemeController(store: store);
      final loading = c.load();
      await c.select(ThemeMode.light);
      await loading;
      expect(c.mode, ThemeMode.light);
    });
  });

  group('about', () {
    test('the version shown is the version in pubspec.yaml', () {
      final pubspec = File('pubspec.yaml').readAsStringSync();
      final version =
          RegExp(r'^version:\s*(\S+)', multiLine: true).firstMatch(pubspec)!;
      expect(appVersion, version.group(1));
    });

    test('the server is named as mock, stage or live', () {
      expect(endpointKind(AppConfig.resolve('')), contains('Mock'));
      expect(endpointKind(AppConfig.resolve('https://stage.oidc.biatec.io')),
          contains('Stage'));
      expect(endpointKind(AppConfig.resolve('https://oidc.biatec.io')),
          contains('Live'));
    });

    testWidgets('shows the version, the server with its kind and the key level',
        (tester) async {
      final rig = SettingsRig();
      await _open(
          tester,
          _screen(rig,
              config: AppConfig.resolve('https://stage.oidc.biatec.io')),
          size: const Size(360, 2000));
      expect(find.text(appVersion), findsOneWidget);
      expect(
          find.textContaining('https://stage.oidc.biatec.io'), findsOneWidget);
      expect(find.textContaining('Stage server'), findsOneWidget);
      expect(
          find.textContaining('trusted execution environment'), findsOneWidget);
    });

    testWidgets('a mock build says nothing is real', (tester) async {
      final rig = SettingsRig();
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      expect(find.textContaining('Nothing here is real'), findsOneWidget);
    });

    testWidgets('a software key is said not to be in secure hardware',
        (tester) async {
      final rig = SettingsRig();
      rig.keys.level = KeySecurityLevel.software;
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      expect(find.textContaining('Not in secure hardware'), findsOneWidget);
    });

    testWidgets('no enrolled device says there is no key', (tester) async {
      final rig = SettingsRig();
      await rig.enrollment.clear();
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      expect(find.text('No key found for this device.'), findsOneWidget);
    });

    testWidgets('the notices page opens', (tester) async {
      final rig = SettingsRig();
      await _open(tester, _screen(rig), size: const Size(360, 2000));
      await tester.tap(find.byKey(const Key('about-licenses')));
      await tester.pumpAndSettle();
      expect(find.byType(LicensePage), findsOneWidget);
    });
  });
}
