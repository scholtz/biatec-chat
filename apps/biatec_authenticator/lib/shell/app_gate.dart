import 'dart:async';

import 'package:flutter/material.dart';

import '../approvals/approval_services.dart';
import '../config.dart';
import '../enrollment/enrollment_controller.dart';
import '../enrollment/enrollment_screen.dart';
import '../enrollment/enrollment_state.dart';
import '../enrollment/observable_enrollment_store.dart';
import '../enrollment/oidc_enrollment_client.dart';
import '../push/push_controller.dart';
import '../security/settings_controller.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'app_shell.dart';
import 'shell_controller.dart';

/// Decides what the app opens on: the enrollment flow until this device is enrolled, active and has shown its recovery code, then the navigation shell.
class AppGate extends StatefulWidget {
  const AppGate({
    super.key,
    required this.config,
    required this.approvals,
    required this.settings,
    required this.shell,
    this.push,
    this.enrollment,
    this.startupTimeout = const Duration(seconds: 8),
  });

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;
  final ShellController shell;
  final PushController? push;

  /// Injected in tests; built from [approvals] and [config] otherwise.
  final EnrollmentController? enrollment;

  /// How long to wait for the stored enrollment before offering a retry, so a keystore that never answers is not an endless spinner.
  final Duration startupTimeout;

  @override
  State<AppGate> createState() => _AppGateState();
}

enum _Startup { loading, ready, stuck, unreadable }

class _AppGateState extends State<AppGate> {
  late final EnrollmentController _enrollment;
  late AppLifecycleListener _lifecycle;
  Timer? _timeout;
  _Startup _startup = _Startup.loading;

  /// Counts start-ups (the first and each retry), so a slow earlier one cannot cancel or finish a later one.
  int _attempt = 0;

  /// Whether the device was active at the last notification, so push registration is asked for when it becomes so.
  bool _wasActive = false;

  /// The store, which announces changes (see [ObservableEnrollmentStore]).
  ObservableEnrollmentStore get _store => widget.approvals.enrollment;

  /// Clears of the store this gate has dealt with.
  int _clearsSeen = 0;

  EnrollmentController _build() => EnrollmentController(
        keys: widget.approvals.keys,
        store: widget.approvals.enrollment,
        client: OidcEnrollmentClient(baseUrl: widget.config.oidcBaseUrl),
      );

  @override
  void initState() {
    super.initState();
    _enrollment = widget.enrollment ?? _build();
    // Coming back to the app is when a delay that ran out becomes visible, or a cancelled enrollment is noticed.
    _lifecycle = AppLifecycleListener(
      // Hidden comes first on desktop and Android; paused on iOS. Either marks the start of a break.
      onHide: widget.shell.appPaused,
      onPause: widget.shell.appPaused,
      onResume: () async {
        widget.shell.appResumed();
        await _refresh();
      },
    );
    // The store says when the enrollment goes (this device cancelled itself in Devices).
    _clearsSeen = _store.clearCount;
    _store.addListener(_onStoreChanged);
    _enrollment.addListener(_onPhase);
    _begin();
  }

  void _retry() {
    setState(() => _startup = _Startup.loading);
    _begin();
  }

  void _begin() {
    final attempt = ++_attempt;
    _timeout?.cancel();
    _timeout = Timer(widget.startupTimeout, () {
      if (mounted && attempt == _attempt && _startup == _Startup.loading) {
        setState(() => _startup = _Startup.stuck);
      }
    });
    _open(attempt).whenComplete(() {
      if (attempt == _attempt) _timeout?.cancel();
    });
  }

  /// The device is enrolled and past its activation delay: the app is usable (a recovery code not yet saved is a notice, not a lock).
  bool get _usable =>
      _enrollment.state.phase == EnrollmentPhase.active ||
      _enrollment.state.phase == EnrollmentPhase.activeRecoveryPending;

  /// A launch that ends in enrollment must not keep a tapped notification for later, and one that will show the shell must. Also asks for push registration once per activation, and closes the recovery code screen when the code was saved.
  void _onPhase() {
    final active = _usable;
    if (_enrollment.state.phase == EnrollmentPhase.active) _closeRecoveryCode();
    // The shell (and the screen inside it) is gone when the device is no longer usable.
    if (!active) _recoveryRoute = null;
    // Enrolling is a swap of widgets, not a navigation, so registering for push is asked for here, once per activation.
    if (active && !_wasActive) widget.push?.registerIfEnrolled();
    _wasActive = active;
    // The stored enrollment is known; do not wait for network work in the rest of the refresh (the recovery code) to show it.
    // Also from "still checking": storage answered late, but it answered.
    if ((_startup == _Startup.loading || _startup == _Startup.stuck) &&
        _enrollment.state.phase != EnrollmentPhase.notEnrolled) {
      setState(() => _startup = _Startup.ready);
    }
    if (_startup != _Startup.ready) return;
    widget.shell.canQueue = active;
    if (!active) widget.shell.dropQueuedOpen();
  }

  /// Lets the controller read the stored enrollment before deciding what to show. A read that fails is not "not enrolled": the user gets a retry instead of the enrollment flow, which would replace a valid enrollment. The timer runs until the gate is ready, so a store that never answers ends in a retry too.
  Future<void> _open(int attempt) async {
    await _refresh();
    // Already showing the app (the state was known before the refresh's network work ended): what happened since, a sign-out say, is not a start-up problem.
    if (!mounted || attempt != _attempt || _startup == _Startup.ready) return;
    if (_enrollment.readFailed &&
        _enrollment.state.phase == EnrollmentPhase.notEnrolled) {
      setState(() => _startup = _Startup.unreadable);
      return;
    }
    setState(() => _startup = _Startup.ready);
    _onPhase();
  }

  /// A failed refresh leaves the state as it was (the controller already treats an unreadable record as not enrolled), so it never becomes an unhandled error.
  Future<void> _refresh() async {
    try {
      await _enrollment.refresh();
    } on Object {
      // Nothing to show; the next resume or the enrollment screen's own refresh tries again.
    }
  }

  /// The stored enrollment was cleared on purpose (this device cancelled itself in Devices): the device is signed out at once, without reading the store, so a read that fails cannot keep it in the shell. A record that is merely missing is not a sign-out: it may be an enrollment kept in memory after a failed write.
  void _onStoreChanged() {
    final clears = _store.clearCount;
    if (clears == _clearsSeen) return;
    _clearsSeen = clears;
    if (!_usable) return;
    _enrollment.signOut();
    // Enrolled again meanwhile: the controller learns what is stored.
    _refresh();
  }

  @override
  void dispose() {
    _timeout?.cancel();
    _lifecycle.dispose();
    _store.removeListener(_onStoreChanged);
    _enrollment.removeListener(_onPhase);
    if (!identical(_enrollment, widget.enrollment)) _enrollment.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    switch (_startup) {
      case _Startup.loading:
        return Scaffold(
          body: Center(
            child: Semantics(
                label: 'Loading', child: const CircularProgressIndicator()),
          ),
        );
      case _Startup.stuck:
        return Scaffold(
          body: ErrorState(
            title: 'Still checking this device',
            message: 'The secure storage on this device is slow to answer. '
                'Your enrollment is not lost.',
            onRetry: _retry,
          ),
        );
      case _Startup.unreadable:
        return Scaffold(
          body: ErrorState(
            title: 'Could not read this device',
            message: 'The secure storage on this device did not open. Your '
                'enrollment is not lost. Try again, and restart the app if it '
                'keeps failing.',
            onRetry: _retry,
          ),
        );
      case _Startup.ready:
        break;
    }
    return ListenableBuilder(
      listenable: _enrollment,
      builder: (context, _) {
        if (_usable) {
          return AppShell(
            config: widget.config,
            approvals: widget.approvals,
            settings: widget.settings,
            controller: widget.shell,
            push: widget.push,
            banner:
                _enrollment.state.phase == EnrollmentPhase.activeRecoveryPending
                    ? _recoveryNotice()
                    : null,
          );
        }
        return EnrollmentScreen(config: widget.config, controller: _enrollment);
      },
    );
  }

  /// The recovery code screen, while it is open inside the Approvals tab.
  Route<void>? _recoveryRoute;

  void _showRecoveryCode() {
    final navigator = widget.shell.navigators[AppTab.approvals]?.currentState;
    if (navigator == null) return;
    // Inside the tab, so the bar stays and a tapped notification still lands where the user is.
    widget.shell.focusApprovals();
    // The banner stays tappable while the screen is open: one screen at a time.
    if (_recoveryRoute?.isActive ?? false) return;
    final route = MaterialPageRoute<void>(
      settings: const RouteSettings(name: ShellController.recoveryCodeRoute),
      builder: (_) =>
          EnrollmentScreen(config: widget.config, controller: _enrollment),
    );
    _recoveryRoute = route;
    navigator.push(route).whenComplete(() {
      if (identical(_recoveryRoute, route)) _recoveryRoute = null;
    });
  }

  void _closeRecoveryCode() {
    final route = _recoveryRoute;
    _recoveryRoute = null;
    if (route != null && route.isActive) {
      widget.shell.navigators[AppTab.approvals]?.currentState
          ?.removeRoute(route);
    }
  }

  /// Shown above every tab while the recovery code has not been saved. It opens the enrollment screen, which shows the code once.
  Widget _recoveryNotice() {
    return Padding(
      padding: const EdgeInsets.all(AppSpacing.s),
      child: StatusBanner(
        severity: StatusSeverity.warning,
        title: 'Save your recovery code',
        message: 'It is shown once and lets you replace this device '
            'if it is lost.',
        actionLabel: 'Show code',
        onAction: _showRecoveryCode,
      ),
    );
  }
}
