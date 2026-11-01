import 'dart:async';

import 'package:flutter/material.dart';

import '../config.dart';
import '../keys/device_key_store.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'enrollment_controller.dart';
import 'enrollment_state.dart';
import 'enrollment_store.dart';
import 'observable_enrollment_store.dart';
import 'oidc_enrollment_client.dart';
import 'widgets/enrollment_problem.dart';
import 'widgets/key_protection_card.dart';
import 'widgets/onboarding_step.dart';
import 'widgets/recovery_code_view.dart';

export 'widgets/recovery_code_view.dart';

/// Enrollment as a guided onboarding: welcome, connect, protect the key (with the activation delay), recovery code, done.
///
/// The controller decides where the user is; this screen only adds the welcome step in front of it and says each state in words.
class EnrollmentScreen extends StatefulWidget {
  const EnrollmentScreen({super.key, required this.config, this.controller});

  final AppConfig config;

  /// Injected in tests; built from [config] otherwise.
  final EnrollmentController? controller;

  @override
  State<EnrollmentScreen> createState() => _EnrollmentScreenState();
}

class _EnrollmentScreenState extends State<EnrollmentScreen> {
  late final EnrollmentController _controller;
  late final TextEditingController _token;
  Timer? _ticker;

  /// Whether the user left the welcome step. A device that already tried (a failed attempt) is past it.
  bool _started = false;

  /// Whether the login token is shown in clear.
  bool _showToken = false;

  @override
  void initState() {
    super.initState();
    _controller = widget.controller ??
        EnrollmentController(
          keys: const PlatformDeviceKeyStore(),
          // The observable store keeps an enrollment whose write failed (the controller relies on it).
          store: ObservableEnrollmentStore(SecureEnrollmentStore()),
          client: OidcEnrollmentClient(baseUrl: widget.config.oidcBaseUrl),
        );
    // The mock accepts the seeded login token; a real build gets the token
    // from the OIDC login.
    _token =
        TextEditingController(text: widget.config.isMock ? 'login-alice' : '');
    _controller.addListener(_forgetToken);
    _controller.refresh();
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      final s = _controller.state;
      // A slow keystore is not asked again every second.
      if (s.phase == EnrollmentPhase.inactive && !_controller.refreshing) {
        _controller.refresh();
      }
    });
  }

  /// True once this screen has seen the device enrolled; the next time it is not enrolled, it signed out.
  bool _wasEnrolled = false;

  /// The login token is for one enrollment: once the device is enrolled (or has signed out again after being so) it is not kept in memory. A refresh that merely finds nothing stored does not touch what the user is typing.
  void _forgetToken() {
    final p = _controller.state.phase;
    final enrolled = p == EnrollmentPhase.inactive ||
        p == EnrollmentPhase.activeRecoveryPending ||
        p == EnrollmentPhase.active;
    if (enrolled) {
      _wasEnrolled = true;
    } else if (p == EnrollmentPhase.notEnrolled && _wasEnrolled) {
      _wasEnrolled = false;
    } else {
      return;
    }
    if (_token.text.isNotEmpty) _token.clear();
  }

  @override
  void dispose() {
    _controller.removeListener(_forgetToken);
    _ticker?.cancel();
    _token.dispose();
    if (widget.controller == null) _controller.dispose();
    super.dispose();
  }

  /// Whole hours and minutes, in words, with the right singular ("1 hour"); changes only when the minute does.
  static String _spokenTimeLeft(Duration d) {
    if (d.inMinutes < 1) return 'Activates in less than a minute';
    String unit(int n, String one) => '$n ${n == 1 ? one : '${one}s'}';
    final m = d.inMinutes.remainder(60);
    return d.inHours == 0
        ? 'Activates in ${unit(m, 'minute')}'
        : 'Activates in ${unit(d.inHours, 'hour')} and ${unit(m, 'minute')}';
  }

  String _format(Duration d) {
    final h = d.inHours;
    final m = d.inMinutes.remainder(60).toString().padLeft(2, '0');
    final s = d.inSeconds.remainder(60).toString().padLeft(2, '0');
    return '$h:$m:$s';
  }

  /// [text] with the login token (when it is long enough to be one) replaced; best effort, for a server that echoes what it was sent.
  String _withoutToken(String text) {
    final token = _token.text.trim();
    return token.length < 6 ? text : text.replaceAll(token, '(token)');
  }

  bool _checking = false;

  /// Asks the device what it has stored. When it has nothing, the user is told, instead of the screen looking unchanged.
  Future<void> _checkAgain() async {
    if (_checking) return;
    setState(() => _checking = true);
    try {
      await _controller.refresh();
    } finally {
      if (mounted) setState(() => _checking = false);
    }
    if (!mounted) return;
    final String? say;
    if (_controller.readFailed) {
      say = 'The storage of this device could not be read. Try again.';
    } else if (_controller.state.phase == EnrollmentPhase.failed) {
      say = 'This app has no enrollment for this device yet.';
    } else {
      say = null;
    }
    if (say != null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(say)));
    }
  }

  OnboardingStep _stepFor(EnrollmentState s) => switch (s.phase) {
        EnrollmentPhase.notEnrolled =>
          _started ? OnboardingStep.connect : OnboardingStep.welcome,
        EnrollmentPhase.failed => OnboardingStep.connect,
        EnrollmentPhase.generatingKey ||
        EnrollmentPhase.enrolling ||
        EnrollmentPhase.inactive =>
          OnboardingStep.protect,
        EnrollmentPhase.activeRecoveryPending => OnboardingStep.recovery,
        EnrollmentPhase.active => OnboardingStep.done,
      };

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Set up this device')),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) {
          final s = _controller.state;
          final step = _stepFor(s);
          return ScrollableCenter(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                StepIndicator(
                    key: const Key('onboarding-progress'), step: step),
                const SizedBox(height: AppSpacing.l),
                ..._content(context, s, step),
              ],
            ),
          );
        },
      ),
    );
  }

  Widget _title(BuildContext context, String text) => Semantics(
        header: true,
        child: Text(text, style: Theme.of(context).textTheme.titleLarge),
      );

  /// The key's protection level in words, once the controller knows it.
  List<Widget> _keyLevel() {
    final level = _controller.keyLevel;
    if (level == null) return const [];
    return [
      const SizedBox(height: AppSpacing.m),
      KeyProtectionCard(level: level),
    ];
  }

  List<Widget> _content(
      BuildContext context, EnrollmentState s, OnboardingStep step) {
    switch (step) {
      case OnboardingStep.welcome:
        return _welcome(context);
      case OnboardingStep.connect:
        return _connect(context, s);
      case OnboardingStep.protect:
        return _protect(context, s);
      case OnboardingStep.recovery:
        return _recovery(context);
      case OnboardingStep.done:
        return _done(context);
    }
  }

  List<Widget> _welcome(BuildContext context) {
    Widget point(IconData icon, String title, String text) => Padding(
          padding: const EdgeInsets.only(bottom: AppSpacing.m),
          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            ExcludeSemantics(child: Icon(icon)),
            const SizedBox(width: AppSpacing.m),
            Expanded(
              child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title, style: Theme.of(context).textTheme.titleSmall),
                    Text(text),
                  ]),
            ),
          ]),
        );
    return [
      KeyedSubtree(
        key: const Key('step-welcome'),
        child: _title(context, 'Welcome to Biatec Authenticator'),
      ),
      const SizedBox(height: AppSpacing.s),
      const Text('This app is the second factor for your Biatec account: it '
          'asks you to approve what matters, on this device, with your '
          'fingerprint or face.'),
      const SizedBox(height: AppSpacing.l),
      point(Icons.key_outlined, 'What it holds',
          'A key that is created in this device\'s secure hardware and never leaves it, and a device token that proves this device to Biatec OIDC.'),
      point(Icons.block, 'What it never holds',
          'Your wallet keys, your funds or your passwords. It cannot move money by itself.'),
      point(Icons.restore, 'If you lose this device',
          'A recovery code, shown once at the end of setup, activates a replacement device.'),
      const SizedBox(height: AppSpacing.s),
      FilledButton(
        key: const Key('onboarding-start'),
        onPressed: () => setState(() => _started = true),
        child: const Text('Get started'),
      ),
    ];
  }

  List<Widget> _connect(BuildContext context, EnrollmentState s) {
    final raw = s.error;
    final problem = raw == null ? null : classifyEnrollmentError(raw);
    final again = problem?.checksAgain ?? false;
    return [
      KeyedSubtree(
        key: const Key('step-connect'),
        child: _title(context, 'Connect this device to your account'),
      ),
      const SizedBox(height: AppSpacing.s),
      const Text('Paste the login token from a fresh Biatec OIDC login. It is '
          'used once, to link this device, and is not kept.'),
      if (problem != null) ...[
        const SizedBox(height: AppSpacing.m),
        StatusBanner(
          key: Key('enrollment-problem-${problem.kind.name}'),
          severity: StatusSeverity.danger,
          title: problem.title,
          // A server's detail text could echo what was sent; the token is never shown back.
          message: _withoutToken(problem.message),
        ),
      ],
      const SizedBox(height: AppSpacing.m),
      TextField(
        key: const Key('login-token'),
        controller: _token,
        // A token is a secret: it is hidden unless the user asks, and the keyboard does not learn it or suggest it.
        obscureText: !_showToken,
        autocorrect: false,
        enableSuggestions: false,
        enableIMEPersonalizedLearning: false,
        decoration: InputDecoration(
          labelText: 'Login token',
          helperText: 'From a fresh Biatec OIDC login',
          suffixIcon: IconButton(
            key: const Key('login-token-toggle'),
            tooltip: _showToken ? 'Hide the token' : 'Show the token',
            icon: Icon(_showToken ? Icons.visibility_off : Icons.visibility),
            onPressed: () => setState(() => _showToken = !_showToken),
          ),
        ),
      ),
      const SizedBox(height: AppSpacing.m),
      FilledButton(
        key: const Key('enroll-button'),
        // Off while the device is being asked, so a second tap does not start a second check.
        onPressed: _checking
            ? null
            : () => again
                ? _checkAgain()
                : _controller.enroll(loginToken: _token.text),
        child: Text(_checking
            ? 'Checking…'
            : problem?.actionLabel ?? 'Connect and create the key'),
      ),
      if (again) ...[
        const SizedBox(height: AppSpacing.s),
        // The server has the device but this app may not: enrolling again is still possible.
        OutlinedButton(
          key: const Key('enroll-again-button'),
          onPressed: () => _controller.enroll(loginToken: _token.text),
          child: const Text('Connect again with this token'),
        ),
      ],
    ];
  }

  List<Widget> _protect(BuildContext context, EnrollmentState s) {
    final theme = Theme.of(context);
    if (s.phase == EnrollmentPhase.inactive) {
      final left = s.remaining(_controller.now);
      return [
        KeyedSubtree(
          key: const Key('step-protect'),
          child: _title(context, 'Device inactive'),
        ),
        const SizedBox(height: AppSpacing.s),
        const Text('A new device can approve requests only after a waiting '
            'period. It protects your account if someone else gets hold of '
            'a login. Until then, actions that need phone approval cannot be '
            'completed here.'),
        const SizedBox(height: AppSpacing.l),
        Center(
          child: Column(children: [
            const ExcludeSemantics(child: Icon(Icons.hourglass_top, size: 40)),
            const SizedBox(height: AppSpacing.s),
            // The spoken label only changes when the minute does, so a screen reader is not given a new sentence every second.
            Semantics(
              container: true,
              label: _spokenTimeLeft(left),
              child: ExcludeSemantics(
                child: Text('Time left: ${_format(left)}',
                    key: const Key('activation-countdown'),
                    style: theme.textTheme.headlineSmall),
              ),
            ),
          ]),
        ),
        ..._keyLevel(),
      ];
    }
    final creating = s.phase == EnrollmentPhase.generatingKey;
    return [
      KeyedSubtree(
        key: const Key('step-protect'),
        child: _title(context, 'Protecting the key'),
      ),
      const SizedBox(height: AppSpacing.s),
      const Text('The key is created in this device\'s secure hardware. It '
          'cannot be exported, and it is used only after your fingerprint or '
          'face.'),
      const SizedBox(height: AppSpacing.l),
      Center(
        child: Column(children: [
          Semantics(label: 'Working', child: const CircularProgressIndicator()),
          const SizedBox(height: AppSpacing.m),
          Text(creating ? 'Creating the device key' : 'Enrolling this device',
              style: theme.textTheme.titleMedium),
        ]),
      ),
      ..._keyLevel(),
    ];
  }

  List<Widget> _recovery(BuildContext context) {
    final code = _controller.recoveryCodeToShow;
    if (code == null) {
      final err = _controller.recoveryCodeError;
      return [
        KeyedSubtree(
          key: const Key('step-recovery'),
          child: _title(context, 'Your recovery code'),
        ),
        const SizedBox(height: AppSpacing.m),
        if (err == null)
          const Center(child: CircularProgressIndicator())
        else
          ErrorState(
            offline: isUnreachableError(err),
            title: 'The recovery code could not be loaded',
            message: err,
            onRetry: _controller.loadRecoveryCode,
          ),
      ];
    }
    return [
      KeyedSubtree(
        key: const Key('step-recovery'),
        child: RecoveryCodeView(
          code: code,
          error: _controller.recoveryCodeError,
          onSaved: _controller.acknowledgeRecoveryCode,
        ),
      ),
    ];
  }

  List<Widget> _done(BuildContext context) {
    final theme = Theme.of(context);
    return [
      KeyedSubtree(
        key: const Key('step-done'),
        child: Column(children: [
          const ExcludeSemantics(
              child: Icon(Icons.check_circle_outline, size: 56)),
          const SizedBox(height: AppSpacing.m),
          Semantics(
            header: true,
            child: Text('This device is enrolled and active.',
                textAlign: TextAlign.center, style: theme.textTheme.titleLarge),
          ),
          const SizedBox(height: AppSpacing.s),
          const Text('You can now approve requests from Biatec apps on this '
              'device.'),
        ]),
      ),
      ..._keyLevel(),
    ];
  }
}
