import 'dart:async';

import 'package:flutter/widgets.dart';

import '../approvals/approval_client.dart';
import '../enrollment/enrollment_store.dart';
import 'push_payload.dart';
import 'push_transport.dart';

/// Connects a [PushTransport] to the app: keeps the token registered at
/// Biatec OIDC, opens the request the user tapped, and tells the pending list
/// to refresh when a push arrives. Polling stays as the fallback, so a lost
/// push or a failed registration only delays a request.
///
/// It exists before the transport does: the app renders first and [start]
/// attaches the transport once Firebase is up.
class PushController extends NavigatorObserver {
  PushController({
    required this.enrollment,
    required this.client,
    required this.onOpenRequest,
    DateTime Function()? clock,
  }) : _clock = clock ?? DateTime.now;

  final EnrollmentStore enrollment;
  final ApprovalClient client;

  /// Opens the approval screen for the request id. The screen fetches the
  /// request from Biatec OIDC; nothing from the push is shown.
  final void Function(String authReqId) onOpenRequest;
  final DateTime Function() _clock;

  /// A registration is sent again after this long, so a server that lost it
  /// (restart, replaced record) gets it back without an app restart.
  static const resendAfter = Duration(minutes: 10);

  PushTransport? _transport;
  final _arrived = StreamController<void>.broadcast();
  final _subs = <StreamSubscription<Object?>>[];

  /// The (device token, push token) pair last registered and when, so
  /// frequent calls don't repeat the request. A new enrollment has a new
  /// device token and registers at once.
  (String, String)? _registered;
  DateTime? _registeredAt;
  Future<void> _queue = Future<void>.value();

  /// Fires when a push for a request arrived while the app is open.
  Stream<void> get requestArrived => _arrived.stream;

  /// Attaches [transport]. Call after the first frame, so a launch payload
  /// finds a mounted navigator.
  Future<void> start(PushTransport transport) async {
    _transport = transport;
    _subs
      ..add(transport.opened.listen(_open))
      ..add(transport.received.listen((data) {
        if (authReqIdFromPush(data) != null) _arrived.add(null);
      }))
      ..add(transport.tokenRefreshes.listen((t) => registerIfEnrolled(t)));
    final launch = await transport.launchPayload();
    if (launch != null) _open(launch);
    await registerIfEnrolled();
  }

  void _open(Map<String, Object?> data) {
    final id = authReqIdFromPush(data);
    if (id != null) onOpenRequest(id);
  }

  // Every navigation (for example back from enrollment, or into the pending
  // list) is a chance to register a device that was not enrolled before.
  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      registerIfEnrolled();

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      registerIfEnrolled();

  /// Registers the push token once this device is enrolled. Safe to call
  /// often; failures are retried on the next call.
  Future<void> registerIfEnrolled([String? token]) =>
      // One registration at a time, in order, so a refreshed token is never
      // overtaken by an older one.
      _queue = _queue.then((_) => _register(token));

  Future<void> _register(String? token) async {
    final transport = _transport;
    if (transport == null) return;
    try {
      final record = await enrollment.read();
      if (record == null) return;
      final pushToken = token ?? await transport.token();
      if (pushToken == null || pushToken.isEmpty) return;
      final pair = (record.deviceToken, pushToken);
      final at = _registeredAt;
      if (_registered == pair &&
          at != null &&
          _clock().difference(at) < resendAfter) {
        return;
      }
      await client.registerPush(
          record.deviceToken, transport.provider, pushToken);
      _registered = pair;
      _registeredAt = _clock();
    } on Object {
      // Polling still finds requests; the next call retries.
    }
  }

  Future<void> dispose() async {
    for (final s in _subs) {
      await s.cancel();
    }
    await _arrived.close();
  }
}
