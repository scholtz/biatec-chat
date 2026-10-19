import 'dart:async';

import '../enrollment/enrollment_store.dart';
import 'push_payload.dart';
import 'push_registration_client.dart';
import 'push_transport.dart';

/// Connects a [PushTransport] to the app: keeps the token registered at
/// Biatec OIDC, opens the request the user tapped, and tells the pending list
/// to refresh when a push arrives. Polling stays as the fallback, so a lost
/// push or a failed registration only delays a request.
class PushController {
  PushController({
    required this.transport,
    required this.enrollment,
    required this.client,
    required this.onOpenRequest,
  });

  final PushTransport transport;
  final EnrollmentStore enrollment;
  final PushRegistrationClient client;

  /// Opens the approval screen for the request id. The screen fetches the
  /// request from Biatec OIDC; nothing from the push is shown.
  final void Function(String authReqId) onOpenRequest;

  final _arrived = StreamController<void>.broadcast();
  final _subs = <StreamSubscription<Object?>>[];

  /// The (device token, push token) pair last registered, so repeated calls
  /// don't repeat the request. A new enrollment has a new device token.
  (String, String)? _registered;
  Future<void> _queue = Future<void>.value();

  /// Fires when a push for a request arrived while the app is open.
  Stream<void> get requestArrived => _arrived.stream;

  Future<void> start() async {
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

  /// Registers the push token once this device is enrolled. Safe to call
  /// often (app start, resume, pending list shown); failures are retried on
  /// the next call.
  Future<void> registerIfEnrolled([String? token]) =>
      // One registration at a time, in order, so a refreshed token is never
      // overtaken by an older one.
      _queue = _queue.then((_) => _register(token));

  Future<void> _register(String? token) async {
    try {
      final record = await enrollment.read();
      if (record == null) return;
      final pushToken = token ?? await transport.token();
      if (pushToken == null || pushToken.isEmpty) return;
      final pair = (record.deviceToken, pushToken);
      if (_registered == pair) return;
      await client.register(
        deviceToken: record.deviceToken,
        provider: transport.provider,
        pushToken: pushToken,
      );
      _registered = pair;
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
