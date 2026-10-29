import 'dart:async';

import 'package:flutter/foundation.dart' show ValueListenable;
import 'package:flutter/material.dart';

import '../theme.dart';
import '../ui/ui.dart';
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
    this.foreground,
  });

  /// False while the app is in the background: no polling then. Null means always in the foreground.
  final ValueListenable<bool>? foreground;

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

  /// The device has no enrollment any more: nothing can be pending for it.
  bool _signedOut = false;
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
    widget.foreground?.addListener(_startPolling);
    widget.refreshOn?.addListener(_onRefreshSignal);
  }

  /// A failing listener is that listener's problem, not a failed load.
  void _tell(int count) {
    try {
      widget.onCount?.call(count);
    } on Object {
      // Ignored on purpose.
    }
  }

  /// A card that covers the list asks for nothing; the list loads when the card closes.
  void _onRefreshSignal() {
    if (!_covered) _refresh();
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
    _poll = null;
    // Coming back after a long break refreshes through [refreshOn]; a short one waits for the next tick.
    if (!(widget.foreground?.value ?? true)) return;
    _poll = Timer.periodic(
        _isActive ? widget.pollEvery : widget.backgroundEvery, (_) {
      // Nobody sees the list under a card while its tab shows; from another tab the badge still counts it.
      if (!(_isActive && _covered)) _refresh();
    });
  }

  /// A parent that passes new signals is listened to from then on.
  @override
  void didUpdateWidget(PendingScreen old) {
    super.didUpdateWidget(old);
    if (old.active != widget.active) {
      old.active?.removeListener(_onActive);
      widget.active?.addListener(_onActive);
    }
    if (old.foreground != widget.foreground) {
      old.foreground?.removeListener(_startPolling);
      widget.foreground?.addListener(_startPolling);
    }
    if (old.refreshOn != widget.refreshOn) {
      old.refreshOn?.removeListener(_onRefreshSignal);
      widget.refreshOn?.addListener(_onRefreshSignal);
    }
    if (old.pushes != widget.pushes) {
      _pushSub?.cancel();
      _pushSub = widget.pushes?.listen((_) => _refresh());
    }
    if (old.active != widget.active ||
        old.foreground != widget.foreground ||
        old.pollEvery != widget.pollEvery ||
        old.backgroundEvery != widget.backgroundEvery) {
      _startPolling();
    }
  }

  @override
  void dispose() {
    widget.active?.removeListener(_onActive);
    widget.foreground?.removeListener(_startPolling);
    widget.refreshOn?.removeListener(_onRefreshSignal);
    _poll?.cancel();
    _pushSub?.cancel();
    super.dispose();
  }

  /// Loads the list. A call while a load runs makes it run once more and returns when that is done, so a pull to refresh spins until fresh data is in.
  Future<void> _refresh() {
    final running = _running;
    if (running != null) {
      _again = true;
      return running;
    }
    return _running = _loadUntilSettled().whenComplete(() => _running = null);
  }

  Future<void>? _running;

  Future<void> _loadUntilSettled() async {
    do {
      _again = false;
      await _loadOnce();
    } while (_again && mounted);
  }

  Future<void> _loadOnce() async {
    try {
      final record = await widget.services.enrollment.read();
      if (record == null) {
        // Signed out: nothing is pending for this device any more.
        if (mounted) {
          setState(() {
            _requests = const [];
            _error = null;
            _offline = false;
            _signedOut = true;
          });
          _tell(0);
        }
        return;
      }
      final list = await widget.services.client.pending(record.deviceToken);
      final now = widget.services.clock();
      if (!mounted) return;
      setState(() {
        _requests = list.where((r) => !r.isExpired(now)).toList();
        _error = null;
        _offline = false;
        _signedOut = false;
      });
      _tell(_requests!.length);
    } on Object catch (e) {
      if (!mounted) return;
      final now = widget.services.clock();
      setState(() {
        _error = e is ApprovalException ? e.message : '$e';
        _offline = e is ApprovalException && e.offline;
        if (_signedOut) {
          // The empty list was the signed-out one; nothing is known now.
          _signedOut = false;
          _requests = null;
        }
        // The last known list stays, but a request that has expired meanwhile is gone.
        _requests = _requests?.where((r) => !r.isExpired(now)).toList();
      });
      final known = _requests;
      if (known != null) _tell(known.length);
    }
  }

  Future<void> _open(ConsentRequest r) async {
    // No polling for a list nobody sees while a request is open.
    _poll?.cancel();
    await Navigator.of(context)
        .push(ApprovalScreen.route(widget.services, r.authReqId));
    if (!mounted) return;
    _startPolling();
    // The card may have been replaced by one on top (a tapped notification); the list loads when that one closes.
    if (!_covered) await _refresh();
  }

  @override
  Widget build(BuildContext context) {
    final requests = _requests;
    final error = _error;
    final Widget content;
    if (requests == null) {
      content = error == null
          ? const LoadingSkeleton()
          : ErrorState(
              key: const Key('pending-error'),
              title: _offline ? null : 'Could not load requests',
              message: error,
              offline: _offline,
              onRetry: _refresh,
            );
    } else if (_signedOut) {
      content = const EmptyState(
        key: Key('pending-signed-out'),
        icon: Icons.link_off,
        title: 'This device is not enrolled',
        message: 'Enroll this device to receive requests.',
      );
    } else if (requests.isEmpty) {
      content = EmptyState(
        icon: Icons.fact_check_outlined,
        title: 'No pending approvals',
        message: 'Requests from Biatec Chat appear here, and this device '
            'is notified when one arrives.',
        actionLabel: 'Check again',
        onAction: _refresh,
      );
    } else {
      content = ListView(children: [
          for (final r in requests)
            ListTile(
              key: Key('pending-${r.authReqId}'),
              leading: Icon(
                  r.client.verified ? Icons.verified : Icons.warning_amber),
              title: Text(summarize(r.detail)),
              subtitle: Text(clientLabel(r.client)),
              trailing: const Icon(Icons.chevron_right),
              onTap: () => _open(r),
            ),
        ]);
    }
    return Scaffold(
      appBar: AppBar(title: const Text('Pending approvals'), actions: [
        IconButton(
            onPressed: _refresh,
            tooltip: 'Refresh',
            icon: const Icon(Icons.refresh)),
      ]),
      // The same two slots in every state, so a banner that comes or goes does not rebuild the list under it.
      body: Column(children: [
        // A failed refresh over a list that is still shown is a banner above it.
        if (error != null && requests != null)
          Padding(
            key: const Key('pending-stale'),
            padding: const EdgeInsets.all(AppSpacing.s),
            child: _offline
                ? OfflineBanner(onRetry: _refresh)
                : StatusBanner(
                    severity: StatusSeverity.warning,
                    title: 'Could not load requests',
                    message: error,
                    actionLabel: 'Try again',
                    onAction: _refresh),
          )
        else
          const SizedBox.shrink(),
        // Pull to refresh in every state (the states scroll, see ScrollableCenter); the first load's skeleton does not scroll, so it has none in effect.
        Expanded(child: RefreshIndicator(onRefresh: _refresh, child: content)),
      ]),
    );
  }
}
