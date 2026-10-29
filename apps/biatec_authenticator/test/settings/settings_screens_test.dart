import 'dart:io';

import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/clients/clients_screen.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/devices/devices_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/security/security_screen.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:biatec_authenticator/security/settings_frame.dart';
import 'package:biatec_authenticator/shell/tab_reload.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

Future<void> _open(WidgetTester tester, Widget screen) async {
  tester.view.physicalSize = const Size(800, 2400);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(MaterialApp(home: screen));
  await tester.pumpAndSettle();
}

void main() {
  group('security settings', () {
    testWidgets('shows the current settings', (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));
      expect(find.text('Security settings'), findsOneWidget);
      expect(find.textContaining('1000.00 EUR'), findsOneWidget);
      expect(tester.widget<SwitchListTile>(find.byKey(const Key('biometric'))).value,
          isTrue);
    });

    testWidgets('turning the biometric requirement off asks this device',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('biometric')));
      await tester.pumpAndSettle();
      expect(rig.gate.asked, 1);
      expect(rig.oidc.settings['biometric_required'], isFalse);
      expect(find.text('Approved on this device and applied.'), findsOneWidget);
    });

    testWidgets('blocking unverified clients is tightening and needs no check',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('block-unverified')));
      await tester.pumpAndSettle();
      expect(rig.gate.asked, 0);
      expect(rig.oidc.settings['block_unverified_clients'], isTrue);
      expect(find.text('Applied.'), findsOneWidget);
    });

    testWidgets('lowering the threshold applies at once, raising is signed',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));

      await tester.tap(find.byKey(const Key('threshold-change')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('eur-field')), '250');
      await tester.tap(find.byKey(const Key('eur-save')));
      await tester.pumpAndSettle();
      expect(rig.oidc.settings['threshold_eur'], '250');
      expect(rig.gate.asked, 0);

      await tester.tap(find.byKey(const Key('threshold-change')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('eur-field')), '900.50');
      await tester.tap(find.byKey(const Key('eur-save')));
      await tester.pumpAndSettle();
      expect(rig.oidc.settings['threshold_eur'], '900.50');
      expect(rig.gate.asked, 1);
      expect(rig.oidc.puts.last['consent'], isNotNull);
    });

    testWidgets('an invalid amount is explained in text and nothing is sent',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('threshold-change')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('eur-field')), 'lots');
      await tester.tap(find.byKey(const Key('eur-save')));
      await tester.pumpAndSettle();
      expect(find.text('Enter an amount like 250 or 250.50.'), findsOneWidget);
      expect(rig.oidc.puts, isEmpty);
    });

    testWidgets('a refused biometric check is shown as text', (tester) async {
      final rig = SettingsRig();
      rig.gate.pass = false;
      await _open(tester, SecurityScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('biometric')));
      await tester.pumpAndSettle();
      expect(find.text('Biometric check did not pass.'), findsOneWidget);
      expect(find.text('Problem'), findsOneWidget);
      expect(rig.oidc.settings['biometric_required'], isTrue);
    });

    testWidgets('offline shows a retry that recovers', (tester) async {
      final oidc = FakeSettingsOidc()..offline = true;
      final rig = SettingsRig(oidc: oidc);
      await _open(tester, SecurityScreen(services: rig.services));
      expect(find.byKey(const Key('unavailable')), findsOneWidget);
      oidc.offline = false;
      await tester.tap(find.text('Try again'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('biometric')), findsOneWidget);
    });

    testWidgets('names the Biatec OIDC server, whatever state the screen is in',
        (tester) async {
      final oidc = FakeSettingsOidc()..offline = true;
      final rig = SettingsRig(oidc: oidc);
      await _open(
          tester,
          SecurityScreen(
              services: rig.services, server: 'https://stage.oidc.biatec.io'));
      expect(find.byKey(const Key('unavailable')), findsOneWidget);
      expect(
          find.descendant(
              of: find.byKey(const Key('oidc-server')),
              matching: find.text('https://stage.oidc.biatec.io')),
          findsOneWidget);
    });

    testWidgets('a change made while a quiet reload runs goes through and is not overwritten',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, SecurityScreen(services: rig.services));
      final c = rig.services.controller();
      await c.load();
      expect(c.settings!.biometricRequired, isTrue);
      final reloading = c.load(quiet: true);
      expect(c.busy, isFalse, reason: 'the controls stay usable');
      final applied = await c.apply({'block_unverified_clients': true});
      await reloading;
      expect(applied, isTrue);
      expect(c.settings!.blockUnverifiedClients, isTrue);
      c.dispose();
    });

    testWidgets('a tab reload refreshes in place and keeps the screen',
        (tester) async {
      final rig = SettingsRig();
      final reload = ValueNotifier(0);
      await _open(
          tester,
          TabReload(
              reload: reload,
              child: SecurityScreen(services: rig.services)));
      final before = tester.state(find.byType(SettingsFrame));
      rig.oidc.settings['biometric_required'] = false;
      reload.value++;
      await tester.pump();
      expect(find.byType(LoadingSkeleton), findsNothing,
          reason: 'the loaded screen stays while it reloads');
      await tester.pumpAndSettle();
      expect(tester.state(find.byType(SettingsFrame)), same(before));
      expect(
          tester.widget<SwitchListTile>(find.byKey(const Key('biometric'))).value,
          isFalse);
    });
  });

  group('device management', () {
    testWidgets('lists devices and cancels a pending enrollment',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, DevicesScreen(services: rig.services));
      expect(find.text('dev-1 (this device)'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.textContaining('Pending.'), findsOneWidget);
      // Only a pending enrollment can be cancelled.
      expect(find.byKey(const Key('cancel-dev-1')), findsNothing);

      await tester.tap(find.byKey(const Key('cancel-dev-2')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('keep')));
      await tester.pumpAndSettle();
      expect(rig.oidc.cancelled, isEmpty);

      await tester.tap(find.byKey(const Key('cancel-dev-2')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('confirm-cancel')));
      await tester.pumpAndSettle();
      expect(rig.oidc.cancelled, ['dev-2']);
      expect(find.byKey(const Key('device-dev-2')), findsNothing);
      expect(find.text('Enrollment cancelled.'), findsOneWidget);
    });
  });

  group('trusted clients', () {
    testWidgets('lifts a client pause with a signed consent', (tester) async {
      final oidc = FakeSettingsOidc();
      oidc.clientById('bank-example')['paused_until'] = oidc.nowSeconds + 3600;
      final rig = SettingsRig(oidc: oidc);
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.byKey(const Key('paused-bank-example')), findsOneWidget);

      await tester.tap(find.byKey(const Key('lift-bank-example')));
      await tester.pumpAndSettle();
      expect(rig.gate.asked, 1);
      expect(oidc.clientById('bank-example')['paused_until'], isNull);
      expect(find.byKey(const Key('paused-bank-example')), findsNothing);
      expect(find.text('Approved on this device and applied.'), findsOneWidget);
    });

    testWidgets('always ask: on applies at once, off is signed', (tester) async {
      final rig = SettingsRig();
      await _open(tester, ClientsScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('always-ask-biatec-chat')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('biatec-chat')['always_ask'], isTrue);
      expect(rig.gate.asked, 0);

      await tester.tap(find.byKey(const Key('always-ask-biatec-chat')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('biatec-chat')['always_ask'], isFalse);
      expect(rig.gate.asked, 1);
    });

    testWidgets('per-client threshold can be set and reset', (tester) async {
      final rig = SettingsRig();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.textContaining('Uses the account threshold'), findsNWidgets(3));

      await tester.tap(find.byKey(const Key('threshold-change-bank-example')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('eur-field')), '100');
      await tester.tap(find.byKey(const Key('eur-save')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('bank-example')['per_client_threshold_eur'], '100');
      expect(rig.gate.asked, 0, reason: 'lower than the account threshold');
      expect(find.textContaining('100 EUR'), findsOneWidget);

      // Back to the account threshold (1000) raises 100: signed.
      await tester.tap(find.byKey(const Key('threshold-reset-bank-example')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('bank-example')['per_client_threshold_eur'], isNull);
      expect(rig.gate.asked, 1);
    });

    testWidgets('an unverified client is labelled in words', (tester) async {
      final rig = SettingsRig();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.textContaining('Unverified client'), findsOneWidget);
      expect(find.text('Verified client'), findsNWidgets(2));
    });
  });

  group('settings API flag', () {
    testWidgets('off: the screens say not available and call nothing',
        (tester) async {
      final rig = SettingsRig();
      final off = SettingsServices(
        client: rig.services.client,
        keys: rig.keys,
        enrollment: rig.enrollment,
        gate: rig.gate,
        enabled: false,
      );
      for (final screen in <Widget>[
        DevicesScreen(services: off),
        ClientsScreen(services: off),
        SecurityScreen(services: off),
      ]) {
        await _open(tester, screen);
        expect(find.byKey(const Key('not-available')), findsOneWidget);
        expect(find.byType(CircularProgressIndicator), findsNothing);
      }
      expect(rig.oidc.puts, isEmpty);
    });

    test('on for the mock, off for any other Biatec OIDC unless set', () {
      expect(AppConfig.resolve('').settingsApi, isTrue);
      expect(AppConfig.resolve(AppConfig.mockOidcBaseUrl).settingsApi, isTrue);
      expect(AppConfig.resolve('https://oidc.example').settingsApi, isFalse);
      expect(
          AppConfig.resolve('https://oidc.example', settingsApi: true)
              .settingsApi,
          isTrue);
    });
  });

  group('routes', () {
    testWidgets('the tabs open the three screens, none of them a placeholder',
        (tester) async {
      tester.view.physicalSize = const Size(800, 2400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final rig = SettingsRig(
          record: EnrollmentRecord(
              kid: 'dev-1',
              deviceToken: FakeSettingsOidc.token,
              activeAt: DateTime.utc(2020),
              attestation: 'att',
              recoveryCodeShown: true));
      final approvals = ApprovalServices(
        client: ApprovalClient(baseUrl: 'http://oidc.test'),
        keys: rig.keys,
        enrollment: rig.enrollment,
        gate: rig.gate,
        history: MemoryHistoryStore(),
      );
      await tester.pumpWidget(AuthenticatorApp(
          config: AppConfig.resolve(''),
          approvals: approvals,
          settings: rig.services));
      await tester.pump();

      for (final (label, type) in [
        ('Devices', DevicesScreen),
        ('Clients', ClientsScreen),
        ('Settings', SecurityScreen),
      ]) {
        await tester.tap(find.descendant(
            of: find.byType(NavigationBar), matching: find.text(label)));
        await tester.pumpAndSettle();
        expect(find.byType(type), findsOneWidget, reason: label);
      }
      await tester.pumpWidget(const SizedBox());
    });

    test('no source file builds a placeholder screen any more', () {
      for (final f in ['lib/main.dart', 'lib/shell/app_shell.dart']) {
        expect(File(f).readAsStringSync(), isNot(contains('PlaceholderScreen')),
            reason: f);
      }
      expect(File('lib/screens/placeholder_screen.dart').existsSync(), isFalse);
    });
  });
}
