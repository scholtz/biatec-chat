/// A push channel (FCM in production builds). Implementations only deliver
/// raw data payloads; [authReqIdFromPush] decides what is used.
abstract class PushTransport {
  /// `fcm` or `apns`, as Biatec OIDC's `PUT /devices/push` expects.
  String get provider;

  /// The current push token, or null when push is unavailable or the user
  /// declined notifications.
  Future<String?> token();

  /// New tokens after the platform rotated the old one.
  Stream<String> get tokenRefreshes;

  /// Payloads received while the app is in the foreground.
  Stream<Map<String, Object?>> get received;

  /// Payloads of notifications the user tapped while the app was running.
  Stream<Map<String, Object?>> get opened;

  /// The payload of the notification that launched the app, if any.
  Future<Map<String, Object?>?> launchPayload();
}
