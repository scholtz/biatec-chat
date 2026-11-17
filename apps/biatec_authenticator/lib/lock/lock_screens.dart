import 'package:material_ui/material_ui.dart';

import '../theme.dart';
import '../update/update_guard.dart';
import '../ui/ui.dart';
import 'biometric_authenticator.dart';
import 'lock_controller.dart';
import 'lock_texts.dart';

/// The screen over a locked app: the name, one sentence and one button. It shows no count, name or code, and offers no other way in.
class LockScreen extends StatelessWidget {
  const LockScreen({super.key, required this.lock});

  final LockController lock;

  String? _problem(LockTexts t, BiometricOutcome? outcome) => switch (outcome) {
        BiometricOutcome.failed => t.text('failed'),
        BiometricOutcome.lockout => t.text('lockout'),
        BiometricOutcome.unavailable => t.text('unavailable'),
        _ => null,
      };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final t = LockTexts.of(Localizations.maybeLocaleOf(context));
    // A computer asks for Windows Hello or the app's PIN, not a fingerprint or a face.
    final desktop = theme.platform == TargetPlatform.windows ||
        theme.platform == TargetPlatform.macOS ||
        theme.platform == TargetPlatform.linux;
    return Scaffold(
      key: const Key('lock-screen'),
      body: SafeArea(
        child: ListenableBuilder(
          listenable: lock,
          builder: (context, _) {
            final busy = lock.state == AppLockState.unlocking;
            final problem = _problem(t, lock.lastOutcome);
            return ScrollableCenter(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Center(child: BrandMark(size: 72)),
                  const SizedBox(height: AppSpacing.m),
                  Semantics(
                    header: true,
                    child: Text('Biatec Authenticator',
                        textAlign: TextAlign.center,
                        style: theme.textTheme.headlineSmall),
                  ),
                  const SizedBox(height: AppSpacing.s),
                  Text(t.text(desktop ? 'lockedDesktop' : 'locked'),
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyLarge),
                  if (lock.integrityWarning.isNotEmpty) ...[
                    const SizedBox(height: AppSpacing.m),
                    StatusBanner(
                      key: const Key('integrity-warning'),
                      severity: StatusSeverity.warning,
                      title: t.text('integrityTitle'),
                      message: t.text('integrityDev'),
                    ),
                  ],
                  if (problem != null) ...[
                    const SizedBox(height: AppSpacing.m),
                    Semantics(
                      liveRegion: true,
                      child: StatusBanner(
                        key: const Key('lock-problem'),
                        severity: StatusSeverity.warning,
                        title: t.text('notUnlocked'),
                        message: problem,
                      ),
                    ),
                  ],
                  const SizedBox(height: AppSpacing.l),
                  FilledButton.icon(
                    key: const Key('unlock'),
                    onPressed: busy ? null : lock.unlock,
                    icon: const Icon(Icons.fingerprint),
                    label: Text(t.text(busy ? 'checking' : 'unlock')),
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
    final t = LockTexts.of(Localizations.maybeLocaleOf(context));
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
                child: Text(t.text('noBiometricTitle'),
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              Text(t.text('noBiometricText')),
              const SizedBox(height: AppSpacing.m),
              Text(t.text('toUseIt')),
              const SizedBox(height: AppSpacing.xs),
              Text(t.text('step1')),
              Text(t.text('step2')),
              Text(t.text('step3')),
              Text(t.text('step4')),
              const SizedBox(height: AppSpacing.l),
              FilledButton.icon(
                key: const Key('check-again'),
                onPressed: lock.unlock,
                icon: const Icon(Icons.refresh),
                label: Text(t.text('checkAgain')),
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
  const BiometricsChangedScreen({super.key, this.restoreBuilder});

  /// Opens the restore-from-backup screen; null hides the action.
  final WidgetBuilder? restoreBuilder;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final t = LockTexts.of(Localizations.maybeLocaleOf(context));
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
                child: Text(t.text('changedTitle'),
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              Text(t.text('changedText')),
              const SizedBox(height: AppSpacing.m),
              Text(t.text('whatToDo')),
              const SizedBox(height: AppSpacing.xs),
              Text(t.text('changedStep1')),
              Text(t.text('changedStep2')),
              if (restoreBuilder != null) ...[
                const SizedBox(height: AppSpacing.l),
                FilledButton.icon(
                  key: const Key('restore-from-backup'),
                  onPressed: () => Navigator.of(context)
                      .push(MaterialPageRoute<void>(builder: restoreBuilder!)),
                  icon: const Icon(Icons.download),
                  label: Text(t.text('restoreFromBackup')),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// The integrity check found that the phone cannot be trusted.
class CompromisedScreen extends StatelessWidget {
  const CompromisedScreen({super.key, required this.lock});

  final LockController lock;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final t = LockTexts.of(Localizations.maybeLocaleOf(context));
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
                child: Text(t.text('compromisedTitle'),
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              Text(t.text('compromisedText')),
              if (lock.canRecheckIntegrity) ...[
                const SizedBox(height: AppSpacing.l),
                OutlinedButton.icon(
                  key: const Key('integrity-recheck'),
                  onPressed: lock.recheckIntegrity,
                  icon: const Icon(Icons.refresh),
                  label: Text(t.text('checkAgain')),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

/// This build is too old, or has expired. Says what to do and nothing else: no way in, no data.
class UpdateRequiredScreen extends StatelessWidget {
  const UpdateRequiredScreen({super.key, required this.lock});

  final LockController lock;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final t = LockTexts.of(Localizations.maybeLocaleOf(context));
    final store = t.text(Theme.of(context).platform == TargetPlatform.iOS ? 'storeApple' : 'storeGoogle');
    final expired = lock.updateReason == UpdateReason.expired;
    return Scaffold(
      key: const Key('update-required'),
      body: SafeArea(
        child: ScrollableCenter(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              ExcludeSemantics(
                  child: Icon(Icons.system_update,
                      size: 64, color: theme.colorScheme.primary)),
              const SizedBox(height: AppSpacing.m),
              Semantics(
                header: true,
                child: Text(t.text('updateTitle'),
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineSmall),
              ),
              const SizedBox(height: AppSpacing.s),
              Text(t.text(expired ? 'updateExpired' : 'updateTooOld'), key: const Key('update-reason')),
              const SizedBox(height: AppSpacing.m),
              Text(t.text('updateHow', {'store': store})),
            ],
          ),
        ),
      ),
    );
  }
}
