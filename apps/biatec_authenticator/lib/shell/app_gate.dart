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

  /// The store, when it announces changes (see [ObservableEnrollmentStore]).
  Listenable? get _storeChanges {
    final store = widget.approvals.enrollment;
    return store is Listenable ? store as Listenable : null;
  }

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
    _storeChanges?.addListener(_recheck);
    if (_storeChanges == null) widget.shell.addListener(_recheck);
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
  void _onPhase() {
    if (_startup != _Startup.ready) return;
    final active = _enrollment.state.phase == EnrollmentPhase.active;
    widget.shell.canQueue = active;
    if (!active) widget.shell.dropQueuedOpen();
  }

  /// Reads the stored enrollment once before deciding what to show. A read that fails is not "not enrolled": the user gets a retry instead of the enrollment flow, which would replace a valid enrollment.
  Future<void> _open(int attempt) async {
    try {
      await widget.approvals.enrollment.read();
    } on Object {
      if (mounted && attempt == _attempt) {
        setState(() => _startup = _Startup.unreadable);
      }
      return;
    }
    await _refresh();
    if (!mounted || attempt != _attempt) return;
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
    if (_enrollment.state.phase != EnrollmentPhase.active) return;
    try {
      final record = await widget.approvals.enrollment.read();
      if (record != null || !mounted) return;
      if (_enrollment.state.phase != EnrollmentPhase.active) return;
      final old = _enrollment..removeListener(_onPhase);
      setState(() => _enrollment = _build()..addListener(_onPhase));
      await _refresh();
      // After the frame that stops using it.
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!identical(old, widget.enrollment)) old.dispose();
      });
    } on Object {
      // An unreadable store is handled by the next refresh.
    }
  }

  @override
  void dispose() {
    _timeout?.cancel();
    _lifecycle.dispose();
    _storeChanges?.removeListener(_recheck);
    widget.shell.removeListener(_recheck);
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
        if (_enrollment.state.phase == EnrollmentPhase.active) {
          return AppShell(
            config: widget.config,
            approvals: widget.approvals,
            settings: widget.settings,
            controller: widget.shell,
            push: widget.push,
          );
        }
        return EnrollmentScreen(config: widget.config, controller: _enrollment);
      },
    );
  }
}
