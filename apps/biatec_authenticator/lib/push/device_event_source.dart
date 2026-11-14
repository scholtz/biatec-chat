import 'device_event.dart';

/// Where [DeviceEvent]s come from: the SignalR channel to Biatec OIDC in the app (see `DeviceChannel`), a fake in tests.
abstract class DeviceEventSource {
  /// Events that arrived and passed validation. A broadcast stream: it keeps working across reconnects.
  Stream<DeviceEvent> get events;

  /// Connects (and reconnects) until [stop]. Safe to call twice.
  Future<void> start();

  /// Closes the connection and makes no further attempts until [start].
  Future<void> stop();
}

/// The optional Apple wake (`--dart-define=APNS_ENABLED=true`): the Apple push token, which Biatec OIDC uses to wake an iPhone whose app is not running. Not a channel: it carries no event.
abstract class ApplePushRegistration {
  /// Asks for notification permission and the device token as hex; null when push is unavailable or the user declined.
  Future<String?> token();

  /// New tokens after the system rotated the old one.
  Stream<String> get tokenRefreshes;
}
