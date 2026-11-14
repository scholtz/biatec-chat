import 'package:biatec_authenticator/channel/local_notifier.dart';
import 'package:biatec_authenticator/settings/channel_strings.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('dexterous.com/flutter/local_notifications');
  final calls = <MethodCall>[];

  setUp(() {
    // The plugin picks its platform class when the app starts on a device; in a test it is set by hand.
    AndroidFlutterLocalNotificationsPlugin.registerWith();
    calls.clear();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add(call);
      return call.method == 'initialize' ? true : null;
    });
  });
  tearDown(() => TestDefaultBinaryMessengerBinding
      .instance.defaultBinaryMessenger
      .setMockMethodCallHandler(channel, null));

  test('the notification for a request has the fixed text and nothing else',
      () async {
    final notifier = LocalDeviceNotifier(strings: () => ChannelStrings.en);
    const id = 'ciba_SECRET_AMOUNT_5000_EUR';
    await notifier.requestArrived(id);
    final show = calls.firstWhere((c) => c.method == 'show');
    final args = (show.arguments as Map).cast<String, Object?>();
    expect(args['title'], 'Approval requested');
    expect(args['body'], 'Open Biatec Authenticator to review');
    expect(args['payload'] ?? '', isEmpty);
    expect('$args', isNot(contains(id)));
    expect('$args', isNot(contains('SECRET')));
    expect(args['id'], notificationIdFor(id));
  });

  test('the Slovak text is fixed too, and a device notification has its own',
      () async {
    final notifier = LocalDeviceNotifier(strings: () => ChannelStrings.sk);
    await notifier.requestArrived('ciba_1');
    await notifier.deviceEnrolled('phone-2');
    await notifier.deviceEnrolled('phone-3');
    final shows = calls.where((c) => c.method == 'show').toList();
    expect((shows[0].arguments as Map)['title'], ChannelStrings.sk.requestTitle);
    expect((shows[1].arguments as Map)['title'], ChannelStrings.sk.deviceTitle);
    // Two added devices, and a request, never share a notification.
    final ids = shows.map((c) => (c.arguments as Map)['id']).toSet();
    expect(ids, hasLength(3));
    expect('${shows[1].arguments}', isNot(contains('phone-2')));
  });

  test('a closed request removes its own notification', () async {
    final notifier = LocalDeviceNotifier(strings: () => ChannelStrings.en);
    await notifier.cancelRequest('ciba_1');
    final cancel = calls.firstWhere((c) => c.method == 'cancel');
    expect(cancel.arguments is Map ? (cancel.arguments as Map)['id'] : cancel.arguments,
        notificationIdFor('ciba_1'));
  });

  test('notification ids are stable, positive and keep 1 free', () {
    expect(notificationIdFor('a'), notificationIdFor('a'));
    expect(notificationIdFor('a'), isNot(notificationIdFor('b')));
    for (final id in ['a', 'ciba_1', 'x' * 128, '']) {
      expect(notificationIdFor(id), greaterThan(1));
      expect(notificationIdFor(id), lessThan(0x80000000));
    }
  });
}
