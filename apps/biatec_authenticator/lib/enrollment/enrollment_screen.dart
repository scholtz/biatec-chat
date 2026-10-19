import 'dart:async';

import 'package:flutter/material.dart';

import '../config.dart';
import '../keys/device_key_store.dart';
import 'enrollment_controller.dart';
import 'enrollment_state.dart';
import 'enrollment_store.dart';
import 'oidc_enrollment_client.dart';

/// Enrollment screen: start, inactive countdown, recovery code.
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

  @override
  void initState() {
    super.initState();
    _controller = widget.controller ??
        EnrollmentController(
          keys: const PlatformDeviceKeyStore(),
          store: SecureEnrollmentStore(),
          client: OidcEnrollmentClient(baseUrl: widget.config.oidcBaseUrl),
        );
    // The mock accepts the seeded login token; a real build gets the token
    // from the OIDC login.
    _token = TextEditingController(
        text: widget.config.isMock ? 'login-alice' : '');
    _controller.refresh();
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      final s = _controller.state;
      if (s.phase == EnrollmentPhase.inactive) _controller.refresh();
    });
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _token.dispose();
    if (widget.controller == null) _controller.dispose();
    super.dispose();
  }

  String _format(Duration d) {
    final h = d.inHours;
    final m = d.inMinutes.remainder(60).toString().padLeft(2, '0');
    final s = d.inSeconds.remainder(60).toString().padLeft(2, '0');
    return '$h:$m:$s';
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Enrollment')),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => Padding(
          padding: const EdgeInsets.all(24),
          child: _body(context, _controller.state),
        ),
      ),
    );
  }

  Widget _body(BuildContext context, EnrollmentState s) {
    final style = Theme.of(context).textTheme.titleMedium;
    switch (s.phase) {
      case EnrollmentPhase.generatingKey:
      case EnrollmentPhase.enrolling:
        return Center(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(
                s.phase == EnrollmentPhase.generatingKey
                    ? 'Creating the device key'
                    : 'Enrolling this device',
                style: style),
          ]),
        );
      case EnrollmentPhase.inactive:
        return Center(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const Icon(Icons.hourglass_top, size: 48),
            const SizedBox(height: 16),
            Text('Device inactive', style: style),
            const SizedBox(height: 8),
            Text(
              'Approvals from this device start in '
              '${_format(s.remaining(_controller.now))}. '
              'Until then, actions that need phone approval cannot be '
              'completed.',
              textAlign: TextAlign.center,
            ),
          ]),
        );
      case EnrollmentPhase.activeRecoveryPending:
        return RecoveryCodeView(
          code: _controller.recoveryCodeToShow!,
          onSaved: _controller.acknowledgeRecoveryCode,
        );
      case EnrollmentPhase.active:
        return Center(
            child: Text('This device is enrolled and active.', style: style));
      case EnrollmentPhase.notEnrolled:
      case EnrollmentPhase.failed:
        return ListView(children: [
          Text(
              'Enroll this device. Its key is created in secure hardware '
              'and cannot be exported.',
              style: style),
          const SizedBox(height: 16),
          TextField(
            controller: _token,
            decoration: const InputDecoration(
                labelText: 'Login token (from a fresh Biatec OIDC login)'),
          ),
          if (s.error != null) ...[
            const SizedBox(height: 12),
            Text(s.error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],
          const SizedBox(height: 16),
          FilledButton(
            onPressed: () => _controller.enroll(loginToken: _token.text),
            child: const Text('Enroll this device'),
          ),
        ]);
    }
  }
}

/// Shows the recovery code once. Leaving requires confirming it was saved.
class RecoveryCodeView extends StatelessWidget {
  const RecoveryCodeView({super.key, required this.code, required this.onSaved});

  final String code;
  final VoidCallback onSaved;

  @override
  Widget build(BuildContext context) {
    return ListView(children: [
      Text('Recovery code', style: Theme.of(context).textTheme.titleLarge),
      const SizedBox(height: 8),
      const Text('Write this code down and keep it safe. It is shown only '
          'once and activates a replacement device immediately.'),
      const SizedBox(height: 16),
      SelectableText(code,
          style: Theme.of(context)
              .textTheme
              .headlineMedium
              ?.copyWith(fontFamily: 'monospace')),
      const SizedBox(height: 16),
      FilledButton(onPressed: onSaved, child: const Text('I have saved it')),
    ]);
  }
}
