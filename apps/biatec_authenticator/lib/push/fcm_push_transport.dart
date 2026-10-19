import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';

import 'push_transport.dart';

/// Firebase options from `--dart-define`s. They identify the Firebase app and
/// are not secrets, but they are per deployment, so none are committed. Push
/// is off unless all four are given.
class FirebasePushConfig {
  static const _apiKey = String.fromEnvironment('FIREBASE_API_KEY');
  static const _appId = String.fromEnvironment('FIREBASE_APP_ID');
  static const _senderId = String.fromEnvironment('FIREBASE_SENDER_ID');
  static const _projectId = String.fromEnvironment('FIREBASE_PROJECT_ID');

  static FirebaseOptions? fromEnvironment() => resolve(
      apiKey: _apiKey,
      appId: _appId,
      senderId: _senderId,
      projectId: _projectId);

  static FirebaseOptions? resolve({
    required String apiKey,
    required String appId,
    required String senderId,
    required String projectId,
  }) {
    if ([apiKey, appId, senderId, projectId].any((v) => v.trim().isEmpty)) {
      return null;
    }
    return FirebaseOptions(
      apiKey: apiKey.trim(),
      appId: appId.trim(),
      messagingSenderId: senderId.trim(),
      projectId: projectId.trim(),
    );
  }
}

/// Push through Firebase Cloud Messaging (FCM also delivers to iOS through
/// APNs, so the provider is always `fcm`). The push token is the Firebase
/// Installation ID that FCM reports after `register()`.
class FcmPushTransport implements PushTransport {
  FcmPushTransport._(this._messaging) {
    _messaging.onRegistered.listen((id) => _latest = id);
  }

  final FirebaseMessaging _messaging;
  String? _latest;

  /// Initializes Firebase and asks for notification permission. Returns null
  /// when push is not configured or cannot start; the app then polls.
  static Future<FcmPushTransport?> create(FirebaseOptions? options) async {
    if (options == null) return null;
    try {
      await Firebase.initializeApp(options: options);
      final messaging = FirebaseMessaging.instance;
      await messaging.requestPermission();
      return FcmPushTransport._(messaging);
    } on Object {
      return null;
    }
  }

  @override
  String get provider => 'fcm';

  @override
  Future<String?> token() async {
    if (_latest != null) return _latest;
    try {
      final next = _messaging.onRegistered.first;
      await _messaging.register();
      return _latest ?? await next.timeout(const Duration(seconds: 20));
    } on Object {
      return null;
    }
  }

  @override
  Stream<String> get tokenRefreshes => _messaging.onRegistered;

  @override
  Stream<Map<String, Object?>> get received =>
      FirebaseMessaging.onMessage.map((m) => m.data);

  @override
  Stream<Map<String, Object?>> get opened =>
      FirebaseMessaging.onMessageOpenedApp.map((m) => m.data);

  @override
  Future<Map<String, Object?>?> launchPayload() async =>
      (await _messaging.getInitialMessage())?.data;
}
