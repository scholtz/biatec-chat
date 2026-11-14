import 'dart:io' show Platform;

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import '../enrollment/enrollment_store.dart';
import '../enrollment/observable_enrollment_store.dart';
import '../settings/channel_strings.dart';

/// What the Android service shows in its permanent notification, and how the service is started. Android only: nothing else can run the channel while the app is closed.
abstract class StayConnectedPlatform {
  /// False off Android: the setting is not offered and nothing starts.
  bool get supported;

  /// The saved choice (default off).
  Future<bool> isEnabled();

  /// Saves the choice. Does not start or stop anything.
  Future<void> saveEnabled(bool enabled);

  /// Starts the foreground service, or keeps it running. [text] is what its notification says; it is also kept for a restart after boot.
  Future<void> startService(ChannelStrings text);

  /// Stops the service, and forgets that it should run after a boot.
  Future<void> stopService();

  /// True when the battery optimisation exemption was never offered and the app is not exempt; it then counts as offered, so it is asked once.
  Future<bool> takeBatteryOffer();

  /// Opens the system screen where the exemption is given.
  Future<void> openBatterySettings();

  /// Asks for the permission to show notifications (the service's notification needs it from Android 13).
  Future<void> requestNotificationPermission();
}

/// The Kotlin side (`StayConnectedPlugin`).
class MethodChannelStayConnected implements StayConnectedPlatform {
  const MethodChannelStayConnected(
      {this.notificationPermission = _noPermission});
  static const _channel = MethodChannel('io.biatec.authenticator/stay_connected');

  /// Asks for the notification permission; the app passes the one from [LocalDeviceNotifier].
  final Future<void> Function() notificationPermission;
  static Future<void> _noPermission() async {}

  @override
  bool get supported =>
      !kIsWeb && defaultTargetPlatform == TargetPlatform.android && Platform.isAndroid;

  @override
  Future<bool> isEnabled() async =>
      await _channel.invokeMethod<bool>('isEnabled') ?? false;

  @override
  Future<void> saveEnabled(bool enabled) =>
      _channel.invokeMethod('saveEnabled', {'enabled': enabled});

  @override
  Future<void> startService(ChannelStrings text) =>
      _channel.invokeMethod('startService', {
        'title': text.serviceTitle,
        'text': text.serviceBody,
        'channelName': text.serviceChannel,
      });

  @override
  Future<void> stopService() => _channel.invokeMethod('stopService');

  @override
  Future<bool> takeBatteryOffer() async =>
      await _channel.invokeMethod<bool>('takeBatteryOffer') ?? false;

  @override
  Future<void> openBatterySettings() =>
      _channel.invokeMethod('openBatterySettings');

  @override
  Future<void> requestNotificationPermission() => notificationPermission();
}

/// The "Stay connected for approvals" setting and what follows from it: the service runs when the setting is on and this device is enrolled, and not otherwise. It follows the enrollment, so a device that is unenrolled stops the service at once, and an enrollment that comes later starts it.
class StayConnectedController extends ChangeNotifier {
  StayConnectedController({
    required this.platform,
    required this.enrollment,
    ChannelStrings Function()? strings,
  }) : _strings = strings ?? ChannelStrings.of;

  final StayConnectedPlatform platform;
  final EnrollmentStore enrollment;
  final ChannelStrings Function() _strings;

  bool _enabled = false;
  bool _loaded = false;
  bool _watching = false;
  Future<void> _queue = Future<void>.value();

  bool get supported => platform.supported;
  bool get enabled => _enabled;
  bool get loaded => _loaded;

  /// Reads the saved choice and brings the service in line with it. Call at start.
  Future<void> load() async {
    if (!supported) return;
    try {
      _enabled = await platform.isEnabled();
    } on Object {
      _enabled = false;
    }
    _loaded = true;
    final store = enrollment;
    if (!_watching && store is ObservableEnrollmentStore) {
      _watching = true;
      store.addListener(_follow);
    }
    notifyListeners();
    await _follow();
  }

  /// The user's choice. Turning it on asks for the notification permission first.
  Future<void> setEnabled(bool value) async {
    if (!supported || value == _enabled) return;
    if (value) {
      try {
        await platform.requestNotificationPermission();
      } on Object {
        // Declined or unavailable: the service still keeps the connection, it just shows no notification.
      }
    }
    _enabled = value;
    notifyListeners();
    try {
      await platform.saveEnabled(value);
    } on Object {
      // Kept for this run; the next load reads the old value.
    }
    await _follow();
  }

  /// Whether to offer the battery exemption now: once, after the first time the setting is turned on.
  Future<bool> shouldOfferBatteryExemption() async {
    if (!supported) return false;
    try {
      return await platform.takeBatteryOffer();
    } on Object {
      return false;
    }
  }

  Future<void> openBatterySettings() => platform.openBatterySettings();

  /// Starts the service when the setting is on and the device is enrolled; stops it otherwise. One step at a time, in order.
  Future<void> _follow() => _queue = _queue.then((_) async {
        if (!supported) return;
        try {
          final enrolled = await enrollment.read() != null;
          if (_enabled && enrolled) {
            await platform.startService(_strings());
          } else {
            await platform.stopService();
          }
        } on Object {
          // The service could not be changed; the next change or start tries again.
        }
      });

  @override
  void dispose() {
    final store = enrollment;
    if (_watching && store is ObservableEnrollmentStore) {
      store.removeListener(_follow);
    }
    super.dispose();
  }
}
