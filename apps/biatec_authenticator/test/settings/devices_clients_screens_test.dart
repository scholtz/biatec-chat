import 'package:biatec_authenticator/clients/clients_screen.dart';
import 'package:biatec_authenticator/devices/devices_screen.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

Future<void> _open(WidgetTester tester, Widget screen,
    {Brightness brightness = Brightness.light,
    double text = 1,
    Size size = const Size(360, 640)}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  tester.platformDispatcher.textScaleFactorTestValue = text;
  addTearDown(tester.view.reset);
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  await tester
      .pumpWidget(MaterialApp(theme: buildTheme(brightness), home: screen));
  await tester.pumpAndSettle();
}

void main() {
  group('devices', () {
    testWidgets('each device says what it is, in words', (tester) async {
      final rig = SettingsRig();
      await _open(tester, DevicesScreen(services: rig.services),
          size: const Size(360, 1600));
      expect(find.byKey(const Key('badge-this-device')), findsOneWidget);
      expect(find.text('This device'), findsOneWidget);
      expect(find.text('Active'), findsOneWidget);
      expect(find.text('Pending'), findsOneWidget);
      expect(find.text('dev-1'), findsOneWidget);
      expect(find.text('dev-2'), findsOneWidget);
      // Only a pending enrollment has a button, and the screen says why.
      expect(find.byKey(const Key('cancel-dev-1')), findsNothing);
      await tester.ensureVisible(find.byKey(const Key('devices-note')));
      await tester.pumpAndSettle();
      expect(find.text('Devices that are in use stay'), findsOneWidget);
    });

    testWidgets('this device shows where its key is kept', (tester) async {
      final rig = SettingsRig();
      await _open(tester, DevicesScreen(services: rig.services));
      expect(find.text('Hardware-protected key'), findsOneWidget);
    });

    testWidgets('a software key is called what it is', (tester) async {
      final rig = SettingsRig();
      rig.keys.level = KeySecurityLevel.software;
      await _open(tester, DevicesScreen(services: rig.services));
      expect(find.text('Software-only key'), findsOneWidget);
    });

    testWidgets('no devices is explained', (tester) async {
      final rig = SettingsRig();
      rig.oidc.devices = [];
      await _open(tester, DevicesScreen(services: rig.services));
      expect(find.byKey(const Key('no-devices')), findsOneWidget);
      expect(find.text('No devices are enrolled.'), findsOneWidget);
    });

    testWidgets('cancelling asks first and names what happens', (tester) async {
      final rig = SettingsRig();
      await _open(tester, DevicesScreen(services: rig.services));
      await tester.ensureVisible(find.byKey(const Key('cancel-dev-2')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('cancel-dev-2')));
      await tester.pumpAndSettle();
      expect(find.text('Cancel this enrollment?'), findsOneWidget);
      expect(find.byKey(const Key('confirm-warning')), findsNothing);
      await tester.tap(find.byKey(const Key('keep')));
      await tester.pumpAndSettle();
      expect(rig.oidc.cancelled, isEmpty);
      expect(find.byKey(const Key('device-dev-2')), findsOneWidget);
    });

    testWidgets('cancelling this device carries a warning', (tester) async {
      final rig = SettingsRig();
      rig.oidc.devices = [
        {
          'kid': 'dev-1',
          'current': true,
          'active': false,
          'active_at': rig.oidc.nowSeconds + 3600,
        },
      ];
      await _open(tester, DevicesScreen(services: rig.services));
      await tester.ensureVisible(find.byKey(const Key('cancel-dev-1')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('cancel-dev-1')));
      await tester.pumpAndSettle();
      expect(
          find.text('Cancel the enrollment of this device?'), findsOneWidget);
      expect(find.byKey(const Key('confirm-warning')), findsOneWidget);
      expect(find.text('You are cancelling the device in your hand.'),
          findsOneWidget);
    });

    for (final brightness in Brightness.values) {
      testWidgets('is usable at 3x text, ${brightness.name}', (tester) async {
        final semantics = tester.ensureSemantics();
        final rig = SettingsRig();
        await _open(tester, DevicesScreen(services: rig.services),
            brightness: brightness, text: 3, size: const Size(320, 4000));
        expect(tester.takeException(), isNull);
        expect(find.byKey(const Key('cancel-dev-2')), findsOneWidget);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
      });
    }

    testWidgets('a device card is read as one item', (tester) async {
      final semantics = tester.ensureSemantics();
      final rig = SettingsRig();
      await _open(tester, DevicesScreen(services: rig.services));
      final node = tester.getSemantics(find.byKey(const Key('kid-dev-1')));
      expect(node.label, contains('Device key id'));
      expect('${node.label} ${node.value}', contains('dev-1'));
      semantics.dispose();
    });
  });

  group('trusted clients', () {
    testWidgets('each client says who it is and whether it is verified',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.byKey(const Key('client-biatec-chat')), findsOneWidget);
      expect(find.text('Verified client'), findsWidgets);
    });

    testWidgets('a paused client shows a banner and lifting asks first',
        (tester) async {
      final oidc = FakeSettingsOidc();
      oidc.clientById('bank-example')['paused_until'] = oidc.nowSeconds + 3600;
      final rig = SettingsRig(oidc: oidc);
      await _open(tester, ClientsScreen(services: rig.services));
      await tester.ensureVisible(find.byKey(const Key('lift-bank-example')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('lift-bank-example')));
      await tester.pumpAndSettle();
      expect(find.text('Lift the pause for Example Bank?'), findsOneWidget);
      expect(find.text('Your phone will ask for you'), findsOneWidget);
      await tester.tap(find.byKey(const Key('keep')));
      await tester.pumpAndSettle();
      expect(rig.gate.asked, 0);
      expect(oidc.clientById('bank-example')['paused_until'], isNotNull);
    });

    testWidgets('turning always-ask off can be declined', (tester) async {
      final rig = SettingsRig();
      rig.oidc.clientById('biatec-chat')['always_ask'] = true;
      await _open(tester, ClientsScreen(services: rig.services));
      await tester.tap(find.byKey(const Key('always-ask-biatec-chat')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('keep')), findsOneWidget);
      await tester.tap(find.byKey(const Key('keep')));
      await tester.pumpAndSettle();
      expect(rig.gate.asked, 0);
      expect(rig.oidc.clientById('biatec-chat')['always_ask'], isTrue);
    });

    testWidgets('no clients is explained', (tester) async {
      final rig = SettingsRig();
      (rig.oidc.settings['clients'] as List).clear();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.byKey(const Key('no-clients')), findsOneWidget);
    });

    testWidgets('the threshold sheet says whether an amount raises or lowers',
        (tester) async {
      final rig = SettingsRig();
      await _open(tester, ClientsScreen(services: rig.services));
      await tester.ensureVisible(
          find.byKey(const Key('threshold-change-bank-example')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('threshold-change-bank-example')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('eur-field')), '5000');
      await tester.pump();
      expect(find.textContaining('This raises it'), findsOneWidget);
      await tester.enterText(find.byKey(const Key('eur-field')), '10');
      await tester.pump();
      expect(find.textContaining('This lowers it'), findsOneWidget);
      await tester.enterText(find.byKey(const Key('eur-field')), 'abc');
      await tester.tap(find.byKey(const Key('eur-save')));
      await tester.pump();
      expect(find.text('Enter an amount like 250 or 250.50.'), findsOneWidget);
      await tester.tap(find.byKey(const Key('eur-cancel')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('bank-example')['per_client_threshold_eur'],
          isNull);
    });

    for (final brightness in Brightness.values) {
      testWidgets('is usable at 3x text, ${brightness.name}', (tester) async {
        final semantics = tester.ensureSemantics();
        final oidc = FakeSettingsOidc();
        oidc.clientById('bank-example')['paused_until'] =
            oidc.nowSeconds + 3600;
        final rig = SettingsRig(oidc: oidc);
        await _open(tester, ClientsScreen(services: rig.services),
            brightness: brightness, text: 3, size: const Size(320, 568));
        expect(tester.takeException(), isNull);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        semantics.dispose();
      });
    }
  });
}
