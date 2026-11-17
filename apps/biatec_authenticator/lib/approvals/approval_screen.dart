import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../keys/device_key_store.dart';
import '../text/rp_strings.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'approval_card.dart';
import 'approval_controller.dart';
import 'approval_texts.dart';
import 'approval_services.dart';
import 'consent_request.dart';
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
    _felt = _controller.phase;
    _controller.addListener(_feel);
  }

  /// Redraws the countdown once a second, but only while the screen can be seen: a card in a tab that is not showing sits under a [TickerMode] that is off (see the shell), and a timer does not know about that, so this follows it.
  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (TickerMode.valuesOf(context).enabled) {
      _ticker ??= Timer.periodic(const Duration(seconds: 1), (_) {
        if (mounted) setState(() {});
      });
    } else {
      _ticker?.cancel();
      _ticker = null;
    }
  }

  late ApprovalPhase _felt;

  /// A touch when the request is decided or fails, once per change of phase.
  void _feel() {
    final phase = _controller.phase;
    if (phase == _felt || !mounted) return;
    // Only what follows the user's own action (the work phase) is felt, not a request that was already decided when it loaded.
    final acted = _felt == ApprovalPhase.working;
    _felt = phase;
    if (!acted) return;
    final haptics = Haptics.instance;
    switch (phase) {
      case ApprovalPhase.approved:
        unawaited(haptics.approve(context));
      case ApprovalPhase.denied:
        unawaited(haptics.deny(context));
      case ApprovalPhase.unavailable:
        unawaited(haptics.error(context));
      default:
        break;
    }
  }

  @override
  void dispose() {
    _controller.removeListener(_feel);
    _ticker?.cancel();
    _code.dispose();
    _controller.dispose();
    super.dispose();
  }

  /// Whether the user can act now: a live, current request that is not being worked on. Offline or expired, both buttons are off (a stale card is never signed, and a deny needs the server too).
  bool _canAct(ApprovalController c) {
    final request = c.request;
    if (request == null || !request.isShowable || !c.canApprove) return false;
    return _canDeny(c);
  }

  /// A request this version cannot show (a 1.0 request) cannot be approved, but it can still be denied: refusing is always safe.
  bool _canDeny(ApprovalController c) {
    final request = c.request;
    if (request == null || request.detail is UnsupportedDetail) return false;
    return c.phase == ApprovalPhase.ready &&
        !c.offline &&
        request.expiresAt.isAfter(c.now);
  }

  @override
  Widget build(BuildContext context) {
    final t = ApprovalTexts.ofContext(context);
    return Scaffold(
      appBar: AppBar(title: Text(t.text('approvalTitle'))),
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
          // A request this app cannot show can be neither approved nor denied (no valid receipt exists for it): it can be dismissed, locally.
          if (c.request?.detail is UnsupportedDetail) {
            return Material(
              elevation: 3,
              child: SafeArea(
                top: false,
                child: Padding(
                  padding: const EdgeInsets.all(AppSpacing.m),
                  child: SizedBox(
                    width: double.infinity,
                    child: FilledButton(
                      key: const Key('dismiss'),
                      onPressed: () => _dismiss(c),
                      child: Text(RpStrings.of(
                              Localizations.maybeLocaleOf(context))
                          .t('dismiss')),
                    ),
                  ),
                ),
              ),
            );
          }
          final can = _canAct(c);
          final canDeny = _canDeny(c);
          return ApprovalActionBar(
            busy: c.phase == ApprovalPhase.working,
            onApprove: can ? () => _approve(c) : null,
            onDeny: canDeny ? c.deny : null,
          );
        },
      ),
    );
  }

  /// Approve, unless another app's window was drawn over part of the screen at the touch (Android 10 and 11): then nothing is sent and the user is told.
  bool _approving = false;

  Future<void> _approve(ApprovalController c) async {
    // A second tap while the check runs does not start a second approval.
    if (_approving) return;
    _approving = true;
    try {
      await _approveChecked(c);
    } finally {
      _approving = false;
    }
  }

  Future<void> _approveChecked(ApprovalController c) async {
    final obscured = await widget.services.touchGuard.partiallyObscured();
    // The request may have expired, been decided elsewhere or the screen gone while the platform answered.
    if (!mounted || !_canAct(c)) return;
    if (obscured) {
      unawaited(Haptics.instance.error(context));
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(
            key: const Key('touch-obscured'),
            content: Text(
                ApprovalTexts.ofContext(context).text('touchObscured'))));
      return;
    }
    await c.approve(_code.text);
  }

  /// Hides the request until it runs out and goes back, with a message: nothing is sent to Biatec OIDC.
  void _dismiss(ApprovalController c) {
    final r = c.request;
    if (r != null) widget.services.dismissed.add(widget.authReqId, r.expiresAt);
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(
          key: const Key('dismissed'),
          content: Text(RpStrings.of(Localizations.maybeLocaleOf(context))
              .t('dismissed'))));
    _close();
  }

  /// Back to the list (or wherever the request was opened from).
  void _close() => Navigator.of(context).maybePop();

  Widget _body(BuildContext context) {
    final c = _controller;
    final t = ApprovalTexts.ofContext(context);
    switch (c.phase) {
      case ApprovalPhase.loading:
        return const LoadingSkeleton(key: Key('approval-loading'), rows: 4);
      case ApprovalPhase.unavailable:
        final error = c.error ?? t.text('notAvailable');
        if (!c.offline && _wasDecided(error)) {
          return ResultView(
            icon: Icons.task_alt,
            title: t.text('alreadyDecidedTitle'),
            message: error,
            actionLabel: t.text('backToApprovals'),
            onAction: _close,
          );
        }
        return ErrorState(
          offline: c.offline,
          title: t.text(c.offline ? 'offlineTitle' : 'notAvailableTitle'),
          message: c.offline ? t.text('offlineNeedsOidc') : error,
          retryLabel: t.text('tryAgain'),
          onRetry: c.load,
        );
      case ApprovalPhase.approved:
        return ResultView(
          icon: Icons.check_circle,
          title: t.text('approvedTitle'),
          message: t.text('approvedBody'),
          actionLabel: t.text('done'),
          onAction: _close,
        );
      case ApprovalPhase.denied:
        return ResultView(
          icon: Icons.block,
          title: t.text('deniedTitle'),
          message: t.text('deniedBody'),
          actionLabel: t.text('done'),
          onAction: _close,
        );
      case ApprovalPhase.codeFailed:
        return ResultView(
          icon: Icons.gpp_bad,
          title: t.text('codeFailedTitle'),
          message: c.error ?? t.text('codeFailedBody'),
          actionLabel: t.text('backToApprovals'),
          onAction: _close,
        );
      case ApprovalPhase.expired:
        return ResultView(
          icon: Icons.timer_off,
          title: t.text('expired'),
          message: t.text('expiredBody'),
          actionLabel: t.text('backToApprovals'),
          onAction: _close,
        );
      case ApprovalPhase.ready:
      case ApprovalPhase.working:
        return _ready(context);
    }
  }

  /// The controller's "already decided" and "no longer pending" (in either language), or a server's words that say so.
  static bool _wasDecided(String error) =>
      error.contains('already decided') ||
      error.contains('no longer pending') ||
      ApprovalTexts.isMessage(error, 'alreadyDecided') ||
      ApprovalTexts.isMessage(error, 'noLongerPending');

  Widget _ready(BuildContext context) {
    final c = _controller;
    final t = ApprovalTexts.ofContext(context);
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
              title: t.text('offlineTitle'),
              message: t
                  .text('offlineBanner', {'error': c.error ?? ''})
                  .replaceAll('  ', ' '),
              actionLabel: t.text('tryAgain'),
              onAction: c.load,
            ),
            const SizedBox(height: AppSpacing.m),
          ] else if (left <= Duration.zero) ...[
            // Both buttons are off; this says why, with the way out.
            StatusBanner(
              key: const Key('approval-expired'),
              severity: StatusSeverity.danger,
              icon: Icons.timer_off,
              title: t.text('expiredBannerTitle'),
              message: t.text('expiredBanner'),
            ),
            const SizedBox(height: AppSpacing.m),
          ] else ...[
            // Approve is off, Deny still works; the way out is to enroll again. An error of the last step is shown as well, never hidden by this banner.
            if (c.keysState == KeysState.approvalKeyInvalidated)
              _keysBanner(
                  const Key('approval-key-invalidated'),
                  t.text('keyInvalidatedTitle'),
                  t.text('keyInvalidatedBody',
                      {'message': ApprovalKeyInvalidated.message}),
                  t,
                  c)
            else if (c.keysState == KeysState.missing)
              _keysBanner(
                  const Key('approval-keys-missing'),
                  t.text('keysMissingTitle'),
                  t.text('keysMissingBody',
                      {'message': ApprovalKeysMissing.message}),
                  t,
                  c),
            if (c.error != null &&
                c.error != ApprovalKeyInvalidated.message &&
                c.error != ApprovalKeysMissing.message) ...[
              _errorBanner(c.error!, t),
              const SizedBox(height: AppSpacing.m),
            ],
          ],
          ApprovalCard(
            request: request,
            trust: widget.services.trustSettings,
            remembered: c.remembered,
            onRemember: busy ? null : c.setRemember,
          ),
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

  Widget _keysBanner(Key key, String title, String message, ApprovalTexts t,
          ApprovalController c) =>
      Padding(
        padding: const EdgeInsets.only(bottom: AppSpacing.m),
        child: StatusBanner(
          key: key,
          severity: StatusSeverity.danger,
          icon: Icons.fingerprint,
          title: title,
          message: message,
          actionLabel: t.text('enrollAgain'),
          onAction: () async {
            await c.startReenrollment();
            if (mounted) _close();
          },
        ),
      );

  /// The controller's and the client's messages after which trying again can help.
  static const _retryable = [
    'couldNotApprove',
    'couldNotDeny',
    'couldNotSign',
    'loadRequestsFailed',
    'loadRequestFailed',
    'refused',
    'unexpectedAnswer',
    'unexpectedResponse',
  ];

  /// A step that did not go through, in words, with what to do next. The message is recognised in either language (the controller makes it in the device's).
  Widget _errorBanner(String error, ApprovalTexts t) {
    bool any(List<String> keys) =>
        keys.any((k) => ApprovalTexts.isMessage(error, k));
    final wrong = any(const ['wrongCode', 'wrongCodeOne', 'wrongCodeN']) ||
        error.startsWith(ApprovalTexts.en.text('wrongCode')) ||
        error.startsWith(ApprovalTexts.sk.text('wrongCode'));
    final changed = any(const ['requestChanged']);
    final cancelled = any(const ['approvalCancelled']);
    final retryable = any(_retryable);
    return StatusBanner(
      key: const Key('approval-error'),
      severity: StatusSeverity.danger,
      title: t.text(wrong
          ? 'errorWrongCodeTitle'
          : changed
              ? 'errorChangedTitle'
              : cancelled
                  ? 'errorCancelledTitle'
                  : 'errorTitle'),
      message: retryable ? t.text('canTryAgain', {'error': error}) : error,
    );
  }
}
