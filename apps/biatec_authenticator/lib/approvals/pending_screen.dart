import 'dart:async';

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
    this.onShown,
  });

  final ApprovalServices services;
  final Duration pollEvery;

  /// Fires when a push announced a new request.
  final Stream<void>? pushes;

  /// Called once when the list is shown (push registration hooks in here).
  final VoidCallback? onShown;

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

  @override
  void initState() {
    super.initState();
    _refresh();
    _startPolling();
    _pushSub = widget.pushes?.listen((_) => _refresh());
    widget.onShown?.call();
  }

  void _startPolling() {
    _poll?.cancel();
    _poll = Timer.periodic(widget.pollEvery, (_) => _refresh());
  }

  @override
  void dispose() {
    _poll?.cancel();
    _pushSub?.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    if (_loading) return;
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
    } on Object catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e is ApprovalException ? e.message : '$e';
        _offline = e is ApprovalException && e.offline;
      });
    } finally {
      _loading = false;
    }
  }

  Future<void> _open(ConsentRequest r) async {
    // No polling for a list nobody sees while a request is open.
    _poll?.cancel();
    await Navigator.of(context).push(MaterialPageRoute<void>(
      builder: (_) =>
          ApprovalScreen(services: widget.services, authReqId: r.authReqId),
    ));
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
