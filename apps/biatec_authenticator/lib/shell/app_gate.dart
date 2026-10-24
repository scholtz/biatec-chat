import 'package:flutter/material.dart';

import '../approvals/approval_services.dart';
import '../config.dart';
import '../enrollment/enrollment_controller.dart';
import '../enrollment/enrollment_screen.dart';
import '../enrollment/enrollment_state.dart';
import '../enrollment/oidc_enrollment_client.dart';
import '../push/push_controller.dart';
import '../security/settings_controller.dart';
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
  });

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;
  final ShellController shell;
  final PushController? push;

  /// Injected in tests; built from [approvals] and [config] otherwise.
  final EnrollmentController? enrollment;

  @override
  State<AppGate> createState() => _AppGateState();
}

class _AppGateState extends State<AppGate> {
  late final EnrollmentController _enrollment;
  late final AppLifecycleListener _lifecycle;
  bool _ready = false;

  @override
  void initState() {
    super.initState();
    _enrollment = widget.enrollment ??
        EnrollmentController(
          keys: widget.approvals.keys,
          store: widget.approvals.enrollment,
          client: OidcEnrollmentClient(baseUrl: widget.config.oidcBaseUrl),
        );
    // Coming back to the app is when a delay that ran out becomes visible.
    _lifecycle = AppLifecycleListener(onResume: _refresh);
    _refresh().whenComplete(() {
      if (mounted) setState(() => _ready = true);
    });
  }

  /// A failed refresh leaves the state as it was (the controller already treats an unreadable record as not enrolled), so it never becomes an unhandled error.
  Future<void> _refresh() async {
    try {
      await _enrollment.refresh();
    } on Object {
      // Nothing to show; the next resume or the enrollment screen's own refresh tries again.
    }
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    if (widget.enrollment == null) _enrollment.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (!_ready) {
      return Scaffold(
        body: Center(
          child: Semantics(
              label: 'Loading', child: const CircularProgressIndicator()),
        ),
      );
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
