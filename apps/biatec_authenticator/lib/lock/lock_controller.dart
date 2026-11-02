import 'dart:async';

import 'package:flutter/widgets.dart';

import '../approvals/biometric_gate.dart';
import '../security/secure_storage.dart';
import 'biometric_authenticator.dart';

/// What the secure area is doing. Only [unlocked] shows it.
enum AppLockState {
  /// Nothing in the secure area is reachable. The state at every start, and after every lock.
  locked,

  /// The biometric prompt is open.
  unlocking,

  /// A strong biometric succeeded and the app is in use.
  unlocked,

  /// No strong biometric is enrolled (or the phone has none): nothing opens, and the screen says how to enroll one.
  noBiometric,

  /// The phone's biometrics changed, so the keys that protect the data stopped working (see [LockController.markBiometricsChanged]).
  biometricsChanged,

  /// The phone cannot be trusted (see [LockController.markCompromised]; set by the integrity check, T-228).
  compromised,
}

/// How long the app may stay in the background before it locks. There is no "never".
enum LockDelay {
  immediately('Immediately', Duration.zero),
  seconds30('After 30 seconds', Duration(seconds: 30)),
  minute1('After 1 minute', Duration(minutes: 1)),
  minutes5('After 5 minutes', Duration(minutes: 5));

  const LockDelay(this.label, this.duration);

  final String label;
  final Duration duration;

  static const LockDelay standard = seconds30;

  /// A saved value that is not one of these gives the default, never a longer delay.
  static LockDelay fromName(String? name) {
    for (final d in values) {
      if (d.name == name) return d;
    }
    return standard;
  }
}

/// Where the lock delay is kept on this phone.
abstract class LockSettingsStore {
  Future<String?> read();
  Future<void> write(String value);
}

class SecureLockSettingsStore implements LockSettingsStore {
  const SecureLockSettingsStore();

  static const _key = 'biatec.lock.delay';

  @override
  Future<String?> read() => deviceBoundStorage.read(key: _key);

  @override
  Future<void> write(String value) =>
      deviceBoundStorage.write(key: _key, value: value);
}

/// Decides when the secure area is open.
///
/// There is exactly one way to [unlocked]: [unlock] with a [BiometricOutcome.success] from the strong-biometric authenticator. No PIN, no fallback, no flag. The app locks at start, after the background delay, when the screen locks, and after [idleTimeout] without a touch; locking runs the [addLockListener] hooks so decrypted secrets leave memory.
class LockController extends ChangeNotifier {
  LockController({
    required this.authenticator,
    LockSettingsStore? store,
    DateTime Function()? clock,
    this.idleTimeout = const Duration(minutes: 2),
    this.idleCheckEvery = const Duration(seconds: 5),
  })  : _store = store ?? const SecureLockSettingsStore(),
        clock = clock ?? DateTime.now {
    _screenLock = authenticator.screenLocked.listen((_) => lock());
  }

  final BiometricAuthenticator authenticator;
  final LockSettingsStore _store;
  final DateTime Function() clock;
  final Duration idleTimeout;
  final Duration idleCheckEvery;

  AppLockState _state = AppLockState.locked;
  AppLockState get state => _state;
  bool get isUnlocked => _state == AppLockState.unlocked;

  LockDelay _delay = LockDelay.standard;
  LockDelay get delay => _delay;

  /// What the lock screen says about the last try, or null.
  BiometricOutcome? _lastOutcome;
  BiometricOutcome? get lastOutcome => _lastOutcome;

  final List<VoidCallback> _listeners = [];
  late final StreamSubscription<void> _screenLock;
  DateTime? _lastActivity;
  DateTime? _backgroundAt;
  bool _disposed = false;

  /// Runs [hook] each time the app locks, to clear decrypted seeds, codes and anything else the secure area holds in memory. Returns what removes it.
  VoidCallback addLockListener(VoidCallback hook) {
    _listeners.add(hook);
    return () => _listeners.remove(hook);
  }

  /// Reads the saved delay. A saved value that is not recognised gives the default (30 seconds).
  Future<void> load() async {
    try {
      _delay = LockDelay.fromName(await _store.read());
    } on Object {
      _delay = LockDelay.standard;
    }
    _notify();
  }

  /// Saves the delay. Asks for a fresh biometric first: it is a security setting. Returns false when that was refused.
  Future<bool> setDelay(LockDelay value) async {
    if (!await reauthenticate('Change when the app locks')) return false;
    _delay = value;
    _notify();
    try {
      await _store.write(value.name);
    } on Object {
      // Not saved: it holds until the app closes, then the default applies.
    }
    return true;
  }

  /// Opens the secure area if a strong biometric succeeds. Every other result leaves it as it was (locked, or one of the screens that say what is wrong).
  Future<void> unlock() async {
    if (_state != AppLockState.locked && _state != AppLockState.noBiometric) {
      return;
    }
    _set(AppLockState.unlocking);
    final status = await authenticator.status();
    if (_disposed) return;
    if (status == BiometricStatus.noneEnrolled ||
        status == BiometricStatus.noHardware) {
      _lastOutcome = null;
      _set(AppLockState.noBiometric);
      return;
    }
    if (status != BiometricStatus.available) {
      _lastOutcome = BiometricOutcome.unavailable;
      _set(AppLockState.locked);
      return;
    }
    final outcome =
        await authenticator.authenticate('Unlock Biatec Authenticator');
    if (_disposed) return;
    // The state may have moved while the prompt was open (the screen locked, biometrics changed): that wins over a late success.
    if (_state != AppLockState.unlocking) return;
    _lastOutcome = outcome;
    switch (outcome) {
      case BiometricOutcome.success:
        _lastActivity = clock();
        _set(AppLockState.unlocked);
      case BiometricOutcome.noneEnrolled:
        _set(AppLockState.noBiometric);
      case BiometricOutcome.cancelled:
      case BiometricOutcome.failed:
      case BiometricOutcome.lockout:
      case BiometricOutcome.unavailable:
        _set(AppLockState.locked);
    }
  }

  /// Locks the secure area and clears what it holds in memory. A phone whose biometrics changed or that is compromised stays on its own screen.
  void lock() {
    if (_disposed) return;
    _backgroundAt = null;
    final wasOpen = _state != AppLockState.locked;
    if (_state == AppLockState.biometricsChanged ||
        _state == AppLockState.compromised) {
      _clearMemory();
      return;
    }
    _clearMemory();
    if (wasOpen || _state == AppLockState.unlocking) _set(AppLockState.locked);
  }

  /// The keys that protect the data stopped working because the phone's biometrics changed. Nothing opens until the data is restored and the device enrolled again.
  void markBiometricsChanged() {
    _clearMemory();
    _set(AppLockState.biometricsChanged);
  }

  /// The integrity check found the phone cannot be trusted. Nothing opens.
  void markCompromised() {
    _clearMemory();
    _set(AppLockState.compromised);
  }

  /// A fresh biometric for one action (an approval, deleting an account, a backup, a security setting). True only on success. A refusal does not lock the app, but nothing is done; while the app is locked it is always false.
  Future<bool> reauthenticate(String reason) async {
    if (_state != AppLockState.unlocked) return false;
    final outcome = await authenticator.authenticate(reason);
    if (outcome != BiometricOutcome.success) return false;
    // Locked while the prompt was open: the success does not count.
    if (_state != AppLockState.unlocked) return false;
    _lastActivity = clock();
    return true;
  }

  // ---- when it locks ----

  /// The app went to the background.
  void appPaused() {
    _backgroundAt ??= clock();
    if (_state == AppLockState.unlocking) {
      // The prompt is the system's own; leaving mid-prompt is a cancel.
      return;
    }
    if (_delay == LockDelay.immediately) lock();
  }

  /// The app came back: it locks if it was away longer than the delay.
  void appResumed() {
    final at = _backgroundAt;
    _backgroundAt = null;
    if (at == null || _state == AppLockState.unlocking) return;
    if (clock().difference(at) >= _delay.duration) lock();
  }

  /// Called for every touch. Resets the idle time.
  void userActivity() {
    if (_state == AppLockState.unlocked) _lastActivity = clock();
  }

  /// Locks when nothing was touched for [idleTimeout]. The [AppLockGate] calls it every [idleCheckEvery] while it is on screen (the controller owns no timer, so none outlives the screen).
  void checkIdle() {
    final at = _lastActivity;
    if (_state != AppLockState.unlocked || at == null) return;
    if (clock().difference(at) >= idleTimeout) lock();
  }

  void _clearMemory() {
    for (final hook in List.of(_listeners)) {
      try {
        hook();
      } on Object {
        // One hook failing must not stop the others from clearing.
      }
    }
  }

  void _set(AppLockState next) {
    if (_state == next) return;
    _state = next;
    _notify();
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _screenLock.cancel();
    super.dispose();
  }
}

/// The approvals and the settings changes ask for their fresh biometric through the lock: while the app is locked the answer is no.
class LockBiometricGate implements BiometricGate {
  const LockBiometricGate(this.lock);

  final LockController lock;

  @override
  Future<bool> confirm(String reason) async {
    try {
      return await lock.reauthenticate(reason);
    } on Object {
      return false;
    }
  }
}

/// Makes the [LockController] available to the Settings screen (the lock delay row).
class LockScope extends InheritedNotifier<LockController> {
  const LockScope(
      {super.key, required LockController lock, required super.child})
      : super(notifier: lock);

  /// Null when the app has no lock (a screen shown on its own in a test).
  static LockController? maybeOf(BuildContext context) =>
      context.dependOnInheritedWidgetOfExactType<LockScope>()?.notifier;
}
