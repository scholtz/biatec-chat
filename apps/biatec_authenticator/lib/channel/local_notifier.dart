import 'package:flutter_local_notifications/flutter_local_notifications.dart';

import '../push/device_event_controller.dart';
import '../settings/channel_strings.dart';

/// The notification id of a request: a stable 31-bit number from the id (FNV-1a), so the notification of one request can be removed without keeping a list. The id itself is never shown.
int notificationIdFor(String authReqId) {
  var h = 0x811c9dc5 & 0x7fffffff;
  for (final unit in authReqId.codeUnits) {
    h = ((h ^ unit) * 0x01000193) & 0x7fffffff;
  }
  return h < 2 ? h + 2 : h;
}

/// Local notifications through `flutter_local_notifications`, with the fixed texts of [ChannelStrings].
class LocalDeviceNotifier implements DeviceNotifier {
  LocalDeviceNotifier({
    FlutterLocalNotificationsPlugin? plugin,
    ChannelStrings Function()? strings,
  })  : _plugin = plugin ?? FlutterLocalNotificationsPlugin(),
        _strings = strings ?? ChannelStrings.of;

  final FlutterLocalNotificationsPlugin _plugin;
  final ChannelStrings Function() _strings;
  Future<void>? _ready;

  static const channelId = 'approvals';

  /// Sets the plugin up once. [onTap] runs when the user taps a notification while the app runs (it brings the Approvals tab forward); without it a tap just opens the app.
  Future<void> initialize({void Function()? onTap}) => _ready ??= _plugin
      .initialize(
        const InitializationSettings(
          android: AndroidInitializationSettings('@mipmap/ic_launcher'),
          iOS: DarwinInitializationSettings(
            requestAlertPermission: false,
            requestBadgePermission: false,
            requestSoundPermission: false,
          ),
        ),
        onDidReceiveNotificationResponse:
            onTap == null ? null : (_) => onTap(),
      )
      .then((_) {});

  /// Asks for the permission to show notifications (Android 13 and later, iOS). Returns whether it is granted.
  Future<bool> requestPermission() async {
    await initialize();
    final android = _plugin.resolvePlatformSpecificImplementation<
        AndroidFlutterLocalNotificationsPlugin>();
    if (android != null) {
      return await android.requestNotificationsPermission() ?? true;
    }
    final ios = _plugin.resolvePlatformSpecificImplementation<
        IOSFlutterLocalNotificationsPlugin>();
    return await ios?.requestPermissions(alert: true, sound: true) ?? false;
  }

  NotificationDetails _details(ChannelStrings s) => NotificationDetails(
        android: AndroidNotificationDetails(
          channelId,
          s.approvalsChannel,
          importance: Importance.high,
          priority: Priority.high,
          // The text is fixed and names nothing, but a locked phone shows less anyway.
          visibility: NotificationVisibility.private,
          category: AndroidNotificationCategory.message,
        ),
        iOS: const DarwinNotificationDetails(),
      );

  @override
  Future<void> requestArrived(String authReqId) async {
    await initialize();
    final s = _strings();
    await _plugin.show(notificationIdFor(authReqId), s.requestTitle,
        s.requestBody, _details(s));
  }

  @override
  Future<void> deviceEnrolled(String kid) async {
    await initialize();
    final s = _strings();
    // One notification per added device, with an id in its own range (the kid picks it and is never shown), so it replaces neither a request's nor another device's.
    await _plugin.show(notificationIdFor('device:$kid'), s.deviceTitle,
        s.deviceBody, _details(s));
  }

  @override
  Future<void> cancelRequest(String authReqId) async {
    await initialize();
    await _plugin.cancel(notificationIdFor(authReqId));
  }
}
