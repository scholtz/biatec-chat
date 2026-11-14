import 'dart:async';

import 'package:flutter/widgets.dart';

import '../approvals/approval_client.dart';
import '../enrollment/enrollment_store.dart';
import '../enrollment/observable_enrollment_store.dart';
import 'device_event.dart';
import 'device_event_source.dart';

/// Local notifications, with text fixed in the app. Nothing from an event goes into a notification: the request id picks which one to dismiss and is never shown.
abstract class DeviceNotifier {
  /// "Approval requested / Open Biatec Authenticator to review".
  Future<void> requestArrived(String authReqId);

  /// "A device was added / Open Biatec Authenticator to review it".
  Future<void> deviceEnrolled();

  /// Removes the notification of a request that was decided, expired or withdrawn.
  Future<void> cancelRequest(String authReqId);
}

/// Connects a [DeviceEventSource] to the app and acts on its events:
///
/// - `consent_request`: the pending list refreshes ([requestArrived]); when the app is not in the foreground the pending requests are fetched over the authenticated call and a notification with fixed text is shown.
/// - `consent_closed`: [onRequestClosed] drops the card, and its notification goes.
/// - `device_enrolled`: [onDevicesChanged] refreshes the device list, and a notification with fixed text is shown.
/// - `settings_changed`: [onSettingsChanged] refreshes the settings.
///
/// The channel only runs while a process does, so a missed event is the normal case and the app polls too (the pending list every 5 seconds while it is open, and on resume). It also owns the optional Apple wake registration (`PUT /devices/push` with provider `apns`), which is the one thing that can reach an iPhone whose app is not running; with no [apple] nothing registers.
class DeviceEventController {
  DeviceEventController({
    required this.enrollment,
    required this.client,
    required this.notifier,
    this.isForeground = _always,
    this.onRequestClosed,
    this.onDevicesChanged,
    this.onSettingsChanged,
    this.apple,
    this.watchLifecycle = true,
    DateTime Function()? clock,
    this.retryAfter = const Duration(minutes: 1),
  }) : _clock = clock ?? DateTime.now;

  static bool _always() => true;

  final EnrollmentStore enrollment;
  final ApprovalClient client;
  final DeviceNotifier notifier;

  /// Whether the app is in front. A request that arrives while it is not shows a notification; one that arrives while it is does not (the pending list shows it).
  final FutureOr<bool> Function() isForeground;
  final void Function(String authReqId)? onRequestClosed;
  final VoidCallback? onDevicesChanged;
  final VoidCallback? onSettingsChanged;

  /// The Apple wake, when the build has it on; null otherwise.
  final ApplePushRegistration? apple;

  /// Whether to follow the app's lifecycle: the source is stopped when the app goes to the background and started when it returns. The Android service runs without a UI and turns this off.
  final bool watchLifecycle;

  /// A registration that failed (offline, a server error) is tried again after this long, until it works.
  final Duration retryAfter;
  final DateTime Function() _clock;

  /// A registration is sent again after this long, so a server that lost it gets it back without an app restart.
  static const resendAfter = Duration(minutes: 10);

  DeviceEventSource? _source;
  final _arrived = StreamController<void>.broadcast();
  final _subs = <StreamSubscription<Object?>>[];
  AppLifecycleListener? _lifecycle;
  Listenable? _watched;

  (String, String)? _registered;
  DateTime? _registeredAt;
  Future<void> _queue = Future<void>.value();
  Timer? _retry;
  Duration? _retryDelay;

  /// Fires when a request arrived while the app is open (or at all): the pending list refreshes, and the app may say so.
  Stream<void> get requestArrived => _arrived.stream;

  /// Attaches [source] and starts it. Call after the first frame.
  Future<void> start(DeviceEventSource source) async {
    // Once: a second start would leave the first listeners behind.
    if (_source != null) return;
    _source = source;
    _subs.add(source.events.listen(_onEvent));
    final registration = apple;
    if (registration != null) {
      _subs.add(registration.tokenRefreshes.listen(registerIfEnrolled));
    }
    if (watchLifecycle) {
      _lifecycle = AppLifecycleListener(
        onResume: _resumed,
        onPause: () => source.stop(),
      );
    }
    final store = enrollment;
    if (store is ObservableEnrollmentStore) {
      _watched = store;
      store.addListener(_enrollmentChanged);
    }
    await source.start();
    await registerIfEnrolled();
  }

  void _resumed() {
    _source?.start();
    registerIfEnrolled();
  }

  void _enrollmentChanged() => registerIfEnrolled();

  void _onEvent(DeviceEvent event) {
    switch (event) {
      case ConsentRequested(:final authReqId):
        unawaited(_consentRequested(authReqId));
      case ConsentClosed(:final authReqId):
        onRequestClosed?.call(authReqId);
        unawaited(_quietly(() => notifier.cancelRequest(authReqId)));
      case DeviceEnrolled():
        onDevicesChanged?.call();
        unawaited(_quietly(notifier.deviceEnrolled));
      case SettingsChanged():
        onSettingsChanged?.call();
    }
  }

  Future<void> _consentRequested(String authReqId) async {
    if (!_arrived.isClosed) _arrived.add(null);
    if (await isForeground()) return;
    try {
      final record = await enrollment.read();
      if (record == null) return;
      try {
        final pending = await client.pending(record.deviceToken);
        // Not in the list: decided or withdrawn already, nothing to ask the user for.
        if (!pending.any((r) => r.authReqId == authReqId)) return;
      } on Object {
        // The list could not be fetched; the notification names nothing, so it is safe to show and the app fetches when opened.
      }
      await notifier.requestArrived(authReqId);
    } on Object {
      // A notification that cannot be shown is not worth a crash: the request is waiting in the app.
    }
  }

  Future<void> _quietly(Future<void> Function() action) async {
    try {
      await action();
    } on Object {
      // See above.
    }
  }

  /// Registers the Apple wake token once this device is enrolled. Safe to call often; failures are retried. Does nothing without [apple].
  Future<void> registerIfEnrolled([String? token]) =>
      // One registration at a time, in order, so a refreshed token is never overtaken by an older one.
      _queue = _queue.then((_) => _register(token));

  Future<void> _register(String? token) async {
    final registration = apple;
    if (registration == null || _source == null) return;
    try {
      final record = await enrollment.read();
      if (record == null) {
        _retry?.cancel();
        _retry = null;
        _retryDelay = null;
        return;
      }
      final pushToken = token ?? await registration.token();
      if (pushToken == null || pushToken.isEmpty) return;
      final pair = (record.deviceToken, pushToken);
      if (_registered == pair && _fresh()) return;
      await client.registerPush(record.deviceToken, 'apns', pushToken);
      _registered = pair;
      _registeredAt = _clock();
      _retry?.cancel();
      _retry = null;
      _retryDelay = null;
    } on Object {
      // Doubling from [retryAfter] to a quarter of an hour, so a server that keeps refusing is not asked every minute for ever.
      final delay = _retryDelay = _retryDelay == null
          ? retryAfter
          : _min(_retryDelay! * 2, const Duration(minutes: 15));
      _retry ??= Timer(delay, () {
        _retry = null;
        if (_source != null) registerIfEnrolled();
      });
    }
  }

  static Duration _min(Duration a, Duration b) => a < b ? a : b;

  bool _fresh() {
    final at = _registeredAt;
    return at != null && _clock().difference(at) < resendAfter;
  }

  Future<void> dispose() async {
    _retry?.cancel();
    _retry = null;
    _watched?.removeListener(_enrollmentChanged);
    _watched = null;
    _lifecycle?.dispose();
    _lifecycle = null;
    for (final s in _subs) {
      await s.cancel();
    }
    _subs.clear();
    await _source?.stop();
    await _arrived.close();
  }
}
