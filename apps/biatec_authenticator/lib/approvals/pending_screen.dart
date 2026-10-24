import 'dart:async';

import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';

import 'approval_client.dart';
import 'approval_controller.dart';
import 'approval_screen.dart';
import 'approval_services.dart';
import 'consent_request.dart';

/// Pending requests for this device's user. Refreshes when [pushes] fires and
/// polls while open as the fallback for lost pushes.
class PendingScreen extends StatefulWidget {
  const PendingScreen({
    super.key,
    required this.services,
    this.pollEvery = const Duration(seconds: 5),
    this.pushes,
    this.onCount,
    this.active,
    this.refreshOn,
    this.backgroundEvery = const Duration(seconds: 30),
  });

  final ApprovalServices services;
  final Duration pollEvery;

  /// Told how many requests are pending each time the list loads (the badge on the Approvals tab).
  final ValueChanged<int>? onCount;

  /// False while another tab is showing: polling slows to [backgroundEvery] (the Approvals badge still has to notice a request), and the list refreshes when it turns true again. Null means always active.
  final ValueListenable<bool>? active;

  /// Refreshes the list whenever it notifies (a card opened from outside the list was closed).
  final Listenable? refreshOn;

  /// Poll interval while [active] is false.
  final Duration backgroundEvery;

  /// Fires when a push announced a new request.
  final Stream<void>? pushes;

  @override
  State<PendingScreen> createState() => _PendingScreenState();
}

class _PendingScreenState extends State<PendingScreen> {
  List<ConsentRequest>? _requests;
  String? _error;
  bool _offline = false;
  bool _loading = false;
  Timer? _poll;
  StreamSubscription<void>? _pushSub;

  /// A refresh was asked for while one was running (for example a push
  /// arrived mid-request); it runs again once the current one ends.
  bool _again = false;

  @override
  void initState() {
    super.initState();
    _refresh();
    _startPolling();
    _pushSub = widget.pushes?.listen((_) => _refresh());
    widget.active?.addListener(_onActive);
    widget.refreshOn?.addListener(_refresh);
  }

  bool get _isActive => widget.active?.value ?? true;

  /// Something (an approval card opened from a notification, say) is on top of the list.
  bool get _covered => ModalRoute.of(context)?.isCurrent == false;

  void _onActive() {
    _startPolling();
    if (_isActive && !_covered) _refresh();
  }

  void _startPolling() {
    _poll?.cancel();
    _poll = Timer.periodic(
        _isActive ? widget.pollEvery : widget.backgroundEvery, (_) {
      // Nobody sees the list under a card while its tab shows; from another tab the badge still counts it.
      if (!(_isActive && _covered)) _refresh();
    });
  }

  @override
  void dispose() {
    widget.active?.removeListener(_onActive);
    widget.refreshOn?.removeListener(_refresh);
    _poll?.cancel();
    _pushSub?.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    if (_loading) {
      _again = true;
      return;
    }
    _loading = true;
    try {
      final record = await widget.services.enrollment.read();
      if (record == null) {
        throw const ApprovalException(
            'Enroll this device to receive requests.');
      }
      final list = await widget.services.client.pending(record.deviceToken);
      final now = widget.services.clock();
      if (!mounted) return;
      setState(() {
        _requests = list.where((r) => !r.isExpired(now)).toList();
        _error = null;
        _offline = false;
      });
      widget.onCount?.call(_requests!.length);
    } on Object catch (e) {
      if (!mounted) return;
      final now = widget.services.clock();
      setState(() {
        _error = e is ApprovalException ? e.message : '$e';
        _offline = e is ApprovalException && e.offline;
        // The last known list stays, but a request that has expired meanwhile is gone.
        _requests = _requests?.where((r) => !r.isExpired(now)).toList();
      });
      final known = _requests;
      if (known != null) widget.onCount?.call(known.length);
    } finally {
      _loading = false;
    }
    if (_again && mounted) {
      _again = false;
      await _refresh();
    }
  }

  Future<void> _open(ConsentRequest r) async {
    // No polling for a list nobody sees while a request is open.
    _poll?.cancel();
    await Navigator.of(context)
        .push(ApprovalScreen.route(widget.services, r.authReqId));
    if (!mounted) return;
    _startPolling();
    await _refresh();
  }

  @override
  Widget build(BuildContext context) {
    final requests = _requests;
    return Scaffold(
      appBar: AppBar(title: const Text('Pending approvals'), actions: [
        IconButton(
            onPressed: _refresh,
            tooltip: 'Refresh',
            icon: const Icon(Icons.refresh)),
      ]),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: ListView(children: [
          if (_error != null)
            ListTile(
              key: const Key('pending-error'),
              leading: Icon(_offline ? Icons.cloud_off : Icons.error_outline),
              title: Text(_offline ? 'Offline' : 'Could not load requests'),
              subtitle: Text(_error!),
            ),
          if (requests == null && _error == null)
            const Padding(
              padding: EdgeInsets.all(24),
              child: Center(child: CircularProgressIndicator()),
            ),
          if (requests != null && requests.isEmpty)
            const ListTile(title: Text('No pending approvals.')),
          for (final r in requests ?? const <ConsentRequest>[])
            ListTile(
              key: Key('pending-${r.authReqId}'),
              leading: Icon(
                  r.client.verified ? Icons.verified : Icons.warning_amber),
              title: Text(summarize(r.detail)),
              subtitle: Text(clientLabel(r.client)),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => _open(r),
            ),
        ]),
      ),
    );
  }
}
