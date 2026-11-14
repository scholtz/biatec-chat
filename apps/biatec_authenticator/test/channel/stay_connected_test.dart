import 'package:biatec_authenticator/channel/stay_connected.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/settings/settings_screen.dart';
import 'package:flutter/material.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/settings/channel_strings.dart';
import 'package:flutter_test/flutter_test.dart';

import '../settings/support.dart';

/// The service starter, as a fake: it records what the app asked of the service.
class FakeStayConnected implements StayConnectedPlatform {
  FakeStayConnected({this.supported = true});

  @override
  final bool supported;
  bool enabled = false;
  int starts = 0;
  int stops = 0;
  bool running = false;
  bool batteryOffered = false;
  int batterySettings = 0;
  int notificationAsks = 0;
  ChannelStrings? lastText;

  @override
  Future<bool> isEnabled() async => enabled;
  @override
  Future<void> saveEnabled(bool value) async => enabled = value;
  @override
  Future<void> startService(ChannelStrings text) async {
    starts++;
    running = true;
    lastText = text;
  }

  @override
  Future<void> stopService() async {
    stops++;
    running = false;
  }

  @override
  Future<bool> takeBatteryOffer() async {
    if (batteryOffered) return false;
    return batteryOffered = true;
  }

  @override
  Future<void> openBatterySettings() async => batterySettings++;
  @override
  Future<void> requestNotificationPermission() async => notificationAsks++;
}

EnrollmentRecord record() => EnrollmentRecord(
    kid: 'alice-device',
    deviceToken: 'dev-token',
    activeAt: DateTime.utc(2020),
    recoveryCodeShown: true);

void main() {
  late FakeStayConnected platform;
  late ObservableEnrollmentStore store;
  late StayConnectedController controller;

  setUp(() {
    platform = FakeStayConnected();
    store = ObservableEnrollmentStore(MemoryEnrollmentStore());
    controller = StayConnectedController(
        platform: platform, enrollment: store, strings: () => ChannelStrings.en);
  });
  tearDown(() => controller.dispose());

  test('the setting is off by default and no service is started', () async {
    await store.write(record());
    await controller.load();
    expect(controller.enabled, isFalse);
    expect(platform.starts, 0);
    expect(platform.running, isFalse);
  });

  test('turned on while enrolled: the service is started, with the fixed text',
      () async {
    await store.write(record());
    await controller.load();
    await controller.setEnabled(true);
    expect(platform.enabled, isTrue);
    expect(platform.starts, 1);
    expect(platform.running, isTrue);
    expect(platform.lastText?.serviceBody, ChannelStrings.en.serviceBody);
    expect(platform.notificationAsks, 1);
  });

  test('turned off: the service is stopped', () async {
    await store.write(record());
    await controller.load();
    await controller.setEnabled(true);
    await controller.setEnabled(false);
    expect(platform.running, isFalse);
    expect(platform.enabled, isFalse);
  });

  test('the service is stopped when the device is unenrolled, and returns when it is enrolled again',
      () async {
    await store.write(record());
    await controller.load();
    await controller.setEnabled(true);
    expect(platform.running, isTrue);
    await store.clear();
    await pumpEventQueue();
    expect(platform.running, isFalse);
    expect(controller.enabled, isTrue, reason: 'the choice is kept');
    await store.write(record());
    await pumpEventQueue();
    expect(platform.running, isTrue);
  });

  test('on but not enrolled: nothing is started', () async {
    platform.enabled = true;
    await controller.load();
    expect(controller.enabled, isTrue);
    expect(platform.starts, 0);
    await controller.setEnabled(false);
    await controller.setEnabled(true);
    expect(platform.starts, 0);
  });

  test('a saved "on" starts the service when the app starts', () async {
    platform.enabled = true;
    await store.write(record());
    await controller.load();
    expect(platform.running, isTrue);
  });

  test('where the service cannot run (not Android), nothing is ever started',
      () async {
    final off = FakeStayConnected(supported: false)..enabled = true;
    final c = StayConnectedController(platform: off, enrollment: store);
    await store.write(record());
    await c.load();
    await c.setEnabled(true);
    expect(off.starts, 0);
    expect(off.stops, 0);
    expect(await c.shouldOfferBatteryExemption(), isFalse);
    c.dispose();
  });

  test('the battery exemption is offered once', () async {
    expect(await controller.shouldOfferBatteryExemption(), isTrue);
    expect(await controller.shouldOfferBatteryExemption(), isFalse);
    await controller.openBatterySettings();
    expect(platform.batterySettings, 1);
  });

  test('English and Slovak have the same texts, and the setting explains the cost',
      () {
    expect(ChannelStrings.sk.keys.toSet(), ChannelStrings.en.keys.toSet());
    for (final s in [ChannelStrings.en, ChannelStrings.sk]) {
      for (final k in s.keys) {
        expect(k, isNotEmpty);
      }
      expect(s.stayTitle, isNotEmpty);
      expect(s.stayBattery, isNotEmpty);
      expect(s.requestTitle, isNot(equals(s.requestBody)));
    }
    expect(ChannelStrings.en.stayOff, contains('when you open the app'));
    expect(ChannelStrings.en.stayBattery, contains('battery'));
    expect(ChannelStrings.sk.stayBattery, contains('batériu'));
    expect(ChannelStrings.sk.stayTitle, isNot(ChannelStrings.en.stayTitle));
  });

  group('the Settings row', () {
    // A plain in-memory store: the observable one persists with real timers, which a widget test's fake clock never fires.
    late MemoryEnrollmentStore mem;
    late StayConnectedController controller;
    late FakeStayConnected platform;
    setUp(() {
      mem = MemoryEnrollmentStore();
      platform = FakeStayConnected();
      controller = StayConnectedController(
          platform: platform,
          enrollment: mem,
          strings: () => ChannelStrings.en);
    });
    tearDown(() => controller.dispose());

    Future<void> open(WidgetTester tester, StayConnectedController? c) async {
      tester.view.physicalSize = const Size(800, 2800);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final rig = SettingsRig();
      await tester.pumpWidget(MaterialApp(
          home: SettingsScreen(
              services: rig.services,
              config: AppConfig.resolve(''),
              stayConnected: c)));
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));

    }

    testWidgets('is off, explains the battery cost, and starts nothing',
        (tester) async {
      await tester.runAsync(() async {
        await mem.write(record());
        await controller.load();
      });
      await open(tester, controller);
      final tile = find.byKey(const Key('stay-connected'));
      expect(tile, findsOneWidget);
      expect(tester.widget<SwitchListTile>(tile).value, isFalse);
      expect(find.textContaining('when you open the app'), findsOneWidget);
      expect(find.textContaining('battery'), findsWidgets);
      expect(platform.starts, 0);
    });

    testWidgets('turning it on starts the service and offers the battery exemption once',
        (tester) async {
      await tester.runAsync(() async {
        await mem.write(record());
        await controller.load();
      });
      await open(tester, controller);
      await tester.tap(find.byKey(const Key('stay-connected')));
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pumpAndSettle();
      expect(platform.running, isTrue);
      expect(find.byKey(const Key('battery-offer')), findsOneWidget);
      await tester.tap(find.byKey(const Key('battery-open')));
      await tester.pumpAndSettle();
      expect(platform.batterySettings, 1);
      // Off and on again: not offered a second time.
      await tester.tap(find.byKey(const Key('stay-connected')));
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pumpAndSettle();
      expect(platform.running, isFalse);
      await tester.tap(find.byKey(const Key('stay-connected')));
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('battery-offer')), findsNothing);
    });

    testWidgets('is not shown where the service cannot run', (tester) async {
      final other = StayConnectedController(
          platform: FakeStayConnected(supported: false), enrollment: mem);
      await open(tester, other);
      expect(find.byKey(const Key('stay-connected')), findsNothing);
      await open(tester, null);
      expect(find.byKey(const Key('stay-connected')), findsNothing);
      other.dispose();
    });
  });
}
