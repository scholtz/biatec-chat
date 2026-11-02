import 'package:flutter/material.dart';

import '../theme.dart';
import '../ui/ui.dart';
import 'biometric_authenticator.dart';
import 'lock_controller.dart';

/// The screen over a locked app: the name, one sentence and one button. It shows no count, name or code, and offers no other way in.
class LockScreen extends StatelessWidget {
  const LockScreen({super.key, required this.lock});

  final LockController lock;

  String? _problem(BiometricOutcome? outcome) => switch (outcome) {
        BiometricOutcome.failed => 'That did not match. Try again.',
        BiometricOutcome.lockout =>
          'Too many tries. Your phone makes you wait before it checks again. '
              'Wait a moment, then try again.',
        BiometricOutcome.unavailable =>
          'Your phone cannot check your fingerprint or face right now. Try '
              'again in a moment.',
        _ => null,
      };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      key: const Key('lock-screen'),
      body: SafeArea(
        child: ListenableBuilder(
          listenable: lock,
          builder: (context, _) {
            final busy = lock.state == AppLockState.unlocking;
            final problem = _problem(lock.lastOutcome);
            return ScrollableCenter(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  ExcludeSemantics(
                      child: Icon(Icons.lock_outline,
                          size: 64, color: theme.colorScheme.primary)),
                  const SizedBox(height: AppSpacing.m),
                  Semantics(
                    header: true,
                    child: Text('Biatec Authenticator',
                        textAlign: TextAlign.center,
                        style: theme.textTheme.headlineSmall),
                  ),
                  const SizedBox(height: AppSpacing.s),
                  Text('Locked. Use your fingerprint or face to open it.',
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyLarge),
                  if (problem != null) ...[
                    const SizedBox(height: AppSpacing.m),
                    Semantics(
                      liveRegion: true,
                      child: StatusBanner(
                        key: const Key('lock-problem'),
                        severity: StatusSeverity.warning,
                        title: 'Not unlocked',
                        message: problem,
                      ),
                    ),
                  ],
                  const SizedBox(height: AppSpacing.l),
                  FilledButton.icon(
                    key: const Key('unlock'),
                    onPressed: busy ? null : lock.unlock,
                    icon: const Icon(Icons.fingerprint),
                    label: Text(busy ? 'Checking' : 'Unlock'),
                  ),
                ],
              ),
            );
          },
        ),
      ),
    );
  }
}

/// A phone with no enrolled strong biometric: how to enroll one, and nothing else. The app never falls back to a weaker lock.
class NoBiometricScreen extends StatelessWidget {
  const NoBiometricScreen({super.key, required this.lock});

  final LockController lock;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      key: const Key('no-biometric'),
      body: SafeArea(
        child: ScrollableCenter(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ExcludeSemantics(
                  child: Icon(Icons.fingerprint,
                      size: 64, color: theme.colorScheme.primary)),
              const SizedBox(height: AppSpacing.m),
              Semantics(
                header: true,
                child: Text('Set up a fingerprint or face',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              const Text(
                  'This app opens only with your fingerprint or face. It does '
                  'not accept a PIN, a pattern or a password instead, so a '
                  'stranger who learns your PIN cannot open it.'),
              const SizedBox(height: AppSpacing.m),
              const Text('To use it:'),
              const SizedBox(height: AppSpacing.xs),
              const Text('1. Open your phone\'s Settings.'),
              const Text('2. Go to Security, then Fingerprint or Face unlock.'),
              const Text('3. Add a fingerprint or your face.'),
              const Text('4. Come back here and tap Check again.'),
              const SizedBox(height: AppSpacing.l),
              FilledButton.icon(
                key: const Key('check-again'),
                onPressed: lock.unlock,
                icon: const Icon(Icons.refresh),
                label: const Text('Check again'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The phone's fingerprints or faces changed, so the hardware keys stopped working. Says what happened and what to do; there is no way in from here.
class BiometricsChangedScreen extends StatelessWidget {
  const BiometricsChangedScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      key: const Key('biometrics-changed'),
      body: SafeArea(
        child: ScrollableCenter(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ExcludeSemantics(
                  child: Icon(Icons.warning_amber_rounded,
                      size: 64, color: theme.colorScheme.error)),
              const SizedBox(height: AppSpacing.m),
              Semantics(
                header: true,
                child: Text('Your fingerprints or face changed',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              const Text(
                  'A fingerprint or face was added or removed on this phone. '
                  'For your safety the keys that protect your codes and your '
                  'approvals stopped working, and the data they protect cannot '
                  'be opened.'),
              const SizedBox(height: AppSpacing.m),
              const Text('What to do:'),
              const SizedBox(height: AppSpacing.xs),
              const Text('1. Restore your two-factor accounts from your '
                  'backup file, or add them again at each service.'),
              const Text('2. Enroll this device again to approve actions. Do '
                  'it from another trusted device if you have one.'),
            ],
          ),
        ),
      ),
    );
  }
}

/// The integrity check found that the phone cannot be trusted.
class CompromisedScreen extends StatelessWidget {
  const CompromisedScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      key: const Key('compromised'),
      body: SafeArea(
        child: ScrollableCenter(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ExcludeSemantics(
                  child: Icon(Icons.gpp_bad_outlined,
                      size: 64, color: theme.colorScheme.error)),
              const SizedBox(height: AppSpacing.m),
              Semantics(
                header: true,
                child: Text('This phone cannot be trusted',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              const Text(
                  'The app found that this phone has been tampered with, '
                  'so it will not open your codes or approvals here. Use '
                  'another phone.'),
            ],
          ),
        ),
      ),
    );
  }
}
