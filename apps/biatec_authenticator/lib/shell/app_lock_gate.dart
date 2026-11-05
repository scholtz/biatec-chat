import 'dart:async';

import 'package:flutter/material.dart';

import '../lock/biometric_authenticator.dart';
import '../lock/lock_controller.dart';
import '../lock/lock_screens.dart';

/// Shows [child] (the secure area) only while the [LockController] is unlocked. Otherwise the widget tree holds a lock screen and nothing of the secure area: the child is not built, so none of its screens exists to be read, scrolled or captured.
///
/// Also reports the app's lifecycle and every touch to the controller (background delay, screen lock, idle time).
class AppLockGate extends StatefulWidget {
  const AppLockGate({super.key, required this.lock, required this.child});

  final LockController lock;
  final Widget child;

  @override
  State<AppLockGate> createState() => _AppLockGateState();
}

class _AppLockGateState extends State<AppLockGate> {
  late final AppLifecycleListener _lifecycle;
  late final Timer _idleCheck;

  @override
  void initState() {
    super.initState();
    _idleCheck = Timer.periodic(
        widget.lock.idleCheckEvery, (_) => widget.lock.checkIdle());
    _lifecycle = AppLifecycleListener(
      // Hidden comes first on desktop and Android; paused on iOS. Either starts a break.
      onHide: widget.lock.appPaused,
      onPause: widget.lock.appPaused,
      onResume: () {
        widget.lock.appResumed();
        // Back to a locked app: ask for the fingerprint straight away (not after a lockout, which only waiting ends).
        if (widget.lock.state == AppLockState.locked &&
            widget.lock.lastOutcome != BiometricOutcome.lockout) {
          widget.lock.unlock();
        }
      },
    );
    // A start is a locked state: ask once the first frame is up.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && widget.lock.state == AppLockState.locked) {
        widget.lock.unlock();
      }
    });
  }

  @override
  void dispose() {
    _idleCheck.cancel();
    _lifecycle.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Listener(
      behavior: HitTestBehavior.translucent,
      onPointerDown: (_) => widget.lock.userActivity(),
      onPointerMove: (_) => widget.lock.userActivity(),
      onPointerUp: (_) => widget.lock.userActivity(),
      child: ListenableBuilder(
        listenable: widget.lock,
        builder: (context, _) => switch (widget.lock.state) {
          AppLockState.unlocked => Stack(children: [
              widget.child,
              // After coming back the check runs first: nothing of the secure area can be read meanwhile.
              if (widget.lock.verifying)
                Positioned.fill(
                  child: ColoredBox(
                    key: const Key('integrity-cover'),
                    color: Theme.of(context).colorScheme.surface,
                    child: const Center(child: CircularProgressIndicator()),
                  ),
                ),
            ]),
          AppLockState.noBiometric => NoBiometricScreen(lock: widget.lock),
          AppLockState.biometricsChanged => const BiometricsChangedScreen(),
          AppLockState.compromised => CompromisedScreen(lock: widget.lock),
          AppLockState.updateRequired =>
            UpdateRequiredScreen(lock: widget.lock),
          AppLockState.locked ||
          AppLockState.unlocking =>
            LockScreen(lock: widget.lock),
        },
      ),
    );
  }
}
