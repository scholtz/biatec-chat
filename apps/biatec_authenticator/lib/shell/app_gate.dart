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
  late EnrollmentController _enrollment;
  late AppLifecycleListener _lifecycle;
  Timer? _timeout;
  _Startup _startup = _Startup.loading;

  /// The check for a signed-out device that is running. A request that comes in meanwhile makes it run once more when it ends, so a sign-out after its read started is still seen.
  Future<void>? _check;
  bool _checkAgain = false;

  /// Counts start-ups (the first and each retry), so a slow earlier one cannot cancel or finish a later one.
  int _attempt = 0;

  /// The start-up whose stored enrollment has been read. What follows may use the network (the recovery code), so it is not timed.
  int _readAttempt = 0;

  /// Whether the device was active at the last notification, so push registration is asked for when it becomes so.
  bool _wasActive = false;

  /// The store, when it announces changes (see [ObservableEnrollmentStore]). Production always has one; without it a sign-out is noticed at the next launch.
  ObservableEnrollmentStore? get _store {
    final store = widget.approvals.enrollment;
    return store is ObservableEnrollmentStore ? store : null;
  }

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
    _lifecycle = AppLifecycleListener(onResume: () async {
      await _recheck();
      await _refresh();
    });
    // The store says when the enrollment goes (this device cancelled itself in Devices); a tab change is the fallback for a store that does not.
    _clearsSeen = _store?.clearCount ?? 0;
    _store?.addListener(_recheck);
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

  /// A launch that ends in enrollment must not keep a tapped notification for later, and one that will show the shell must.
  /// The device is enrolled and past its activation delay: the app is usable (a recovery code not yet saved is a notice, not a lock).
  bool get _usable =>
      _enrollment.state.phase == EnrollmentPhase.active ||
      _enrollment.state.phase == EnrollmentPhase.activeRecoveryPending;

  void _onPhase() {
    final active = _usable;
    // Enrolling is a swap of widgets, not a navigation, so registering for push is asked for here, once per activation.
    if (active && !_wasActive) widget.push?.registerIfEnrolled();
    _wasActive = active;
    // The stored enrollment is known; do not wait for network work in the rest of the refresh to show it.
    if (_startup == _Startup.loading &&
        _readAttempt == _attempt &&
        _enrollment.state.phase != EnrollmentPhase.notEnrolled) {
      setState(() => _startup = _Startup.ready);
    }
    if (_startup != _Startup.ready) return;
    widget.shell.canQueue = active;
    if (!active) widget.shell.dropQueuedOpen();
  }

  /// Reads the stored enrollment once before deciding what to show. A read that fails is not "not enrolled": the user gets a retry instead of the enrollment flow, which would replace a valid enrollment.
  Future<void> _open(int attempt) async {
    EnrollmentRecord? stored;
    try {
      stored = await widget.approvals.enrollment.read();
      if (attempt == _attempt) {
        _readAttempt = attempt;
        _timeout?.cancel();
      }
    } on Object {
      if (mounted && attempt == _attempt) {
        setState(() => _startup = _Startup.unreadable);
      }
      return;
    }
    await _refresh();
    if (!mounted || attempt != _attempt) return;
    // The controller reads the store again and counts a failed read as "not enrolled"; a record that was just read says otherwise.
    if (stored != null &&
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

  /// While the shell shows, looks for the stored enrollment having gone. The controller would otherwise keep its in-memory record and write it back, so it is replaced by a fresh one, which finds the device not enrolled.
  Future<void> _recheck() {
    final running = _check;
    if (running != null) {
      _checkAgain = true;
      return running;
    }
    return _check = _recheckLoop();
  }

  Future<void> _recheckLoop() async {
    do {
      _checkAgain = false;
      await _doRecheck();
    } while (_checkAgain && mounted);
    _check = null;
  }

  Future<void> _doRecheck() async {
    final store = _store;
    // Only a deliberate clear signs the device out; a record that is merely missing may be an enrollment kept in memory.
    final clears = store?.clearCount;
    if (store == null || clears == _clearsSeen) return;
    if (!_usable) {
      _clearsSeen = clears!;
      return;
    }
    // Before the read: a refresh that is already running must not put the record back.
    _enrollment.discardHeldRecord();
    try {
      final record = await widget.approvals.enrollment.read();
      if (!mounted) return;
      // Dealt with: a read that failed is tried again at the next notification or resume.
      _clearsSeen = clears!;
      if (record != null) {
        // Cleared and enrolled again meanwhile: the controller learns what is stored.
        await _refresh();
        return;
      }
      final old = _enrollment..removeListener(_onPhase);
      setState(() => _enrollment = _build()..addListener(_onPhase));
      await _refresh();
      // After the frame that stops using it.
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!identical(old, widget.enrollment)) old.dispose();
      });
    } on Object {
      // The same clear is looked at again next time (the held record stays dropped).
    }
  }

  @override
  void dispose() {
    _timeout?.cancel();
    _lifecycle.dispose();
    _store?.removeListener(_recheck);
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
            banner: _recoveryNotice(),
          );
        }
        return EnrollmentScreen(config: widget.config, controller: _enrollment);
      },
    );
  }

  /// Shown above every tab while the recovery code has not been saved. It opens the enrollment screen, which shows the code once.
  Widget? _recoveryNotice() {
    return ListenableBuilder(
      listenable: _enrollment,
      builder: (context, _) {
        if (_enrollment.state.phase != EnrollmentPhase.activeRecoveryPending) {
          return const SizedBox.shrink();
        }
        return Padding(
          padding: const EdgeInsets.all(AppSpacing.s),
          child: StatusBanner(
            severity: StatusSeverity.warning,
            title: 'Save your recovery code',
            message: 'It is shown once and lets you replace this device '
                'if it is lost.',
            actionLabel: 'Show code',
            onAction: () => Navigator.of(context, rootNavigator: true).push(
              MaterialPageRoute<void>(
                builder: (_) => EnrollmentScreen(
                    config: widget.config, controller: _enrollment),
              ),
            ),
          ),
        );
      },
    );
  }
}
