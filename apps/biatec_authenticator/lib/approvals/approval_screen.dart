import 'dart:async';

import 'package:flutter/material.dart';

import '../theme.dart';
import '../ui/ui.dart';
import 'approval_card.dart';
import 'approval_controller.dart';
import 'approval_services.dart';
import 'widgets/approval_actions.dart';
import 'widgets/binding_code_field.dart';
import 'widgets/expiry.dart';
import 'widgets/result_view.dart';

/// One request: the card, the binding code, and Approve and Deny in a bar that stays at the bottom.
class ApprovalScreen extends StatefulWidget {
  const ApprovalScreen({
    super.key,
    required this.services,
    required this.authReqId,
  });

  final ApprovalServices services;
  final String authReqId;

  /// Route names carry the request id, so the push handler can tell whether
  /// a request is already open.
  static const routePrefix = '/approval/';

  static MaterialPageRoute<void> route(
          ApprovalServices services, String authReqId) =>
      MaterialPageRoute<void>(
        settings: RouteSettings(name: '$routePrefix$authReqId'),
        builder: (_) =>
            ApprovalScreen(services: services, authReqId: authReqId),
      );

  @override
  State<ApprovalScreen> createState() => _ApprovalScreenState();
}

class _ApprovalScreenState extends State<ApprovalScreen> {
  late final ApprovalController _controller;
  final _code = TextEditingController();
  Timer? _ticker;

  @override
  void initState() {
    super.initState();
    _controller = widget.services.controllerFor(widget.authReqId)..load();
    // Redraws the countdown.
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _code.dispose();
    _controller.dispose();
    super.dispose();
  }

  /// Whether the user can act now: a live, current request that is not being worked on. Offline or expired, both buttons are off (a stale card is never signed, and a deny needs the server too).
  bool _canAct(ApprovalController c) {
    final request = c.request;
    if (request == null) return false;
    return c.phase == ApprovalPhase.ready &&
        !c.offline &&
        request.expiresAt.isAfter(c.now);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Approval')),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => _body(context),
      ),
      // Outside the scrolling body, so it is on screen whatever the text size.
      bottomNavigationBar: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) {
          final c = _controller;
          final shown = c.phase == ApprovalPhase.ready ||
              c.phase == ApprovalPhase.working;
          if (!shown) return const SizedBox.shrink();
          final can = _canAct(c);
          return ApprovalActionBar(
            busy: c.phase == ApprovalPhase.working,
            onApprove: can ? () => c.approve(_code.text) : null,
            onDeny: can ? c.deny : null,
          );
        },
      ),
    );
  }

  /// Back to the list (or wherever the request was opened from).
  void _close() => Navigator.of(context).maybePop();

  Widget _body(BuildContext context) {
    final c = _controller;
    switch (c.phase) {
      case ApprovalPhase.loading:
        return const LoadingSkeleton(key: Key('approval-loading'), rows: 4);
      case ApprovalPhase.unavailable:
        final error = c.error ?? 'This request is not available.';
        if (!c.offline && _wasDecided(error)) {
          return ResultView(
            icon: Icons.task_alt,
            title: 'Already decided',
            message: error,
            actionLabel: 'Back to approvals',
            onAction: _close,
          );
        }
        return ErrorState(
          offline: c.offline,
          title: c.offline ? null : 'This request is not available',
          message: c.offline
              ? 'Offline. Approvals need a connection to Biatec OIDC.'
              : error,
          onRetry: c.load,
        );
      case ApprovalPhase.approved:
        return ResultView(
          icon: Icons.check_circle,
          title: 'Approved.',
          message: 'Your approval was sent to Biatec OIDC, which continues '
              'the action. You can close this screen.',
          actionLabel: 'Done',
          onAction: _close,
        );
      case ApprovalPhase.denied:
        return ResultView(
          icon: Icons.block,
          title: 'Denied.',
          message:
              'Nothing was approved. Your decision was sent to Biatec OIDC.',
          actionLabel: 'Done',
          onAction: _close,
        );
      case ApprovalPhase.codeFailed:
        return ResultView(
          icon: Icons.gpp_bad,
          title: 'Three wrong codes. The request was refused.',
          message: c.error ??
              'Start the action again to get a new request and a new code.',
          actionLabel: 'Back to approvals',
          onAction: _close,
        );
      case ApprovalPhase.expired:
        return ResultView(
          icon: Icons.timer_off,
          title: 'This request has expired.',
          message: 'Nothing was approved. Start the action again to get a new '
              'request.',
          actionLabel: 'Back to approvals',
          onAction: _close,
        );
      case ApprovalPhase.ready:
      case ApprovalPhase.working:
        return _ready(context);
    }
  }

  static bool _wasDecided(String error) =>
      error.contains('already decided') || error.contains('no longer pending');

  Widget _ready(BuildContext context) {
    final c = _controller;
    final request = c.request!;
    final left = request.expiresAt.difference(c.now);
    final busy = c.phase == ApprovalPhase.working;
    return ListView(
        padding: const EdgeInsets.all(AppSpacing.m),
        // Scrolling the card away from the keyboard closes it.
        keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
        children: [
          if (c.offline) ...[
            StatusBanner(
              key: const Key('approval-error'),
              severity: StatusSeverity.warning,
              icon: Icons.cloud_off,
              title: 'You are offline',
              message: 'Offline. Nothing was approved. ${c.error ?? ''} '
                      'Approve and Deny are off until Biatec OIDC can be reached '
                      'again.'
                  .replaceAll('  ', ' '),
              actionLabel: 'Try again',
              onAction: c.load,
            ),
            const SizedBox(height: AppSpacing.m),
          ] else if (left <= Duration.zero) ...[
            // Both buttons are off; this says why, with the way out.
            const StatusBanner(
              key: Key('approval-expired'),
              severity: StatusSeverity.danger,
              icon: Icons.timer_off,
              title: 'This request has expired',
              message: 'Approve and Deny are off: this request can no longer '
                  'be approved or denied. Go back and start the action again.',
            ),
            const SizedBox(height: AppSpacing.m),
          ] else if (c.error != null) ...[
            _errorBanner(c.error!),
            const SizedBox(height: AppSpacing.m),
          ],
          ApprovalCard(request: request),
          const SizedBox(height: AppSpacing.m),
          ExpiryRow(request: request, now: c.now),
          const SizedBox(height: AppSpacing.m),
          BindingCodeField(
            controller: _code,
            enabled: !busy,
            attemptsRemaining: c.attemptsRemaining,
          ),
          // Room for the keyboard and the bar over the last field.
          const SizedBox(height: AppSpacing.l),
        ]);
  }

  /// A step that did not go through, in words, with what to do next.
  Widget _errorBanner(String error) {
    final wrong = error.startsWith('Wrong code');
    final changed = error.startsWith('The request changed');
    final retryable = error.startsWith('Could not') ||
        error.startsWith('Refused') ||
        error.startsWith('Unexpected');
    return StatusBanner(
      key: const Key('approval-error'),
      severity: StatusSeverity.danger,
      title: wrong
          ? 'Wrong code'
          : changed
              ? 'The request changed'
              : 'Could not complete',
      message: retryable ? '$error You can try again.' : error,
    );
  }
}
