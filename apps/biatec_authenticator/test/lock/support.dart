import 'dart:async';

import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:flutter_test/flutter_test.dart';

/// A scripted strong-biometric authenticator.
class FakeAuthenticator implements BiometricAuthenticator {
  BiometricStatus statusResult = BiometricStatus.available;
  BiometricOutcome outcome = BiometricOutcome.success;

  /// When set, [authenticate] waits for it (a prompt that is still open).
  Completer<BiometricOutcome>? pending;

  int prompts = 0;
  final List<String> reasons = [];
  final StreamController<void> _locked = StreamController.broadcast();

  void screenLocks() => _locked.add(null);

  @override
  Stream<void> get screenLocked => _locked.stream;

  @override
  Future<BiometricStatus> status() async => statusResult;

  @override
  Future<BiometricOutcome> authenticate(String reason) async {
    prompts++;
    reasons.add(reason);
    final p = pending;
    if (p != null) return p.future;
    return outcome;
  }
}

class MemoryLockStore implements LockSettingsStore {
  String? value;
  bool failReads = false;

  @override
  Future<String?> read() async {
    if (failReads) throw StateError('locked');
    return value;
  }

  @override
  Future<void> write(String v) async => value = v;
}

/// A lock whose fake biometric succeeds, already unlocked, so a test of another screen starts inside the app. It is disposed when the test ends.
Future<LockController> unlockedLock({DateTime Function()? clock}) async {
  final lock = LockController(
      authenticator: FakeAuthenticator(),
      store: MemoryLockStore(),
      clock: clock);
  addTearDown(lock.dispose);
  await lock.unlock();
  return lock;
}
