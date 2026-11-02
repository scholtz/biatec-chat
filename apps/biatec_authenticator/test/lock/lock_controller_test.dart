import 'dart:async';

import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/lock/lock_controller.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:biatec_authenticator/approvals/biometric_gate.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';

import '../settings/support.dart' show SettingsRig;
import 'support.dart';

class Clock {
  DateTime now = DateTime.utc(2026, 11, 2, 10);
  void advance(Duration d) => now = now.add(d);
}

({
  LockController lock,
  FakeAuthenticator auth,
  MemoryLockStore store,
  Clock clock
}) make({LockDelay? delay}) {
  final auth = FakeAuthenticator();
  final store = MemoryLockStore();
  final clock = Clock();
  final lock =
      LockController(authenticator: auth, store: store, clock: () => clock.now);
  addTearDown(lock.dispose);
  return (lock: lock, auth: auth, store: store, clock: clock);
}

Future<LockController> unlockedWith(
    ({
      LockController lock,
      FakeAuthenticator auth,
      MemoryLockStore store,
      Clock clock
    }) r,
    LockDelay delay) async {
  r.store.value = delay.name;
  await r.lock.load();
  await r.lock.unlock();
  expect(r.lock.state, AppLockState.unlocked);
  return r.lock;
}

SettingsController _controllerWith(SettingsRig rig, BiometricGate gate) =>
    SettingsController(
        client: rig.services.client,
        keys: rig.keys,
        enrollment: rig.enrollment,
        gate: gate,
        clock: rig.services.clock,
        nonce: rig.services.nonce);

void main() {
  group('only a biometric success opens the secure area', () {
    test('a start is a locked state', () {
      expect(make().lock.state, AppLockState.locked);
    });

    for (final outcome in [
      BiometricOutcome.failed,
      BiometricOutcome.cancelled,
      BiometricOutcome.lockout,
      BiometricOutcome.unavailable,
    ]) {
      test('$outcome leaves it locked', () async {
        final r = make();
        r.auth.outcome = outcome;
        await r.lock.unlock();
        expect(r.lock.state, AppLockState.locked);
        expect(r.lock.isUnlocked, isFalse);
        expect(r.lock.lastOutcome, outcome);
      });
    }

    test('no enrollment shows the enroll screen and never opens', () async {
      final r = make();
      r.auth.outcome = BiometricOutcome.noneEnrolled;
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.noBiometric);
    });

    for (final status in [
      BiometricStatus.noneEnrolled,
      BiometricStatus.noHardware,
    ]) {
      test('$status shows the enroll screen without asking', () async {
        final r = make();
        r.auth.statusResult = status;
        await r.lock.unlock();
        expect(r.lock.state, AppLockState.noBiometric);
        expect(r.auth.prompts, 0);
      });
    }

    test('a biometric that cannot be used right now stays locked', () async {
      final r = make();
      r.auth.statusResult = BiometricStatus.unavailable;
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.locked);
      expect(r.auth.prompts, 0);
    });

    test('once enrolled, checking again opens it', () async {
      final r = make();
      r.auth.statusResult = BiometricStatus.noneEnrolled;
      await r.lock.unlock();
      r.auth.statusResult = BiometricStatus.available;
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.unlocked);
    });

    test('a success opens it, and nothing else does', () async {
      final r = make();
      await r.lock.unlock();
      expect(r.lock.state, AppLockState.unlocked);
    });

    test('a success that arrives after the screen locked does not open it',
        () async {
      final r = make();
      r.auth.pending = Completer<BiometricOutcome>();
      final opening = r.lock.unlock();
      await Future<void>.delayed(Duration.zero);
      expect(r.lock.state, AppLockState.unlocking);
      r.auth.screenLocks();
      await Future<void>.delayed(Duration.zero);
      r.auth.pending!.complete(BiometricOutcome.success);
      await opening;
      expect(r.lock.state, AppLockState.locked);
    });

    test('two taps do not open two prompts', () async {
      final r = make();
      r.auth.pending = Completer<BiometricOutcome>();
      final a = r.lock.unlock();
      final b = r.lock.unlock();
      await Future<void>.delayed(Duration.zero);
      expect(r.auth.prompts, 1);
      r.auth.pending!.complete(BiometricOutcome.failed);
      await Future.wait([a, b]);
    });

    test('biometrics changed and compromised cannot be unlocked', () async {
      for (final mark in [
        (LockController l) => l.markBiometricsChanged(),
        (LockController l) => l.markCompromised(),
      ]) {
        final r = make();
        await r.lock.unlock();
        mark(r.lock);
        final state = r.lock.state;
        expect(state, isNot(AppLockState.unlocked));
        await r.lock.unlock();
        expect(r.lock.state, state);
        r.lock.lock();
        expect(r.lock.state, state);
      }
    });
  });

  group('when it locks', () {
    for (final delay in LockDelay.values) {
      test('${delay.name}: locks once the app was away that long', () async {
        final r = make();
        final lock = await unlockedWith(r, delay);
        lock.appPaused();
        if (delay == LockDelay.immediately) {
          expect(lock.state, AppLockState.locked,
              reason:
                  'locked as the app leaves, so the switcher shows no data');
          return;
        }
        r.clock.advance(delay.duration - const Duration(seconds: 1));
        lock.appResumed();
        expect(lock.state, AppLockState.unlocked,
            reason: 'back one second before the delay');
        lock.appPaused();
        r.clock.advance(delay.duration);
        lock.appResumed();
        expect(lock.state, AppLockState.locked);
      });
    }

    test('the default delay is 30 seconds and there is no "never"', () {
      expect(LockDelay.standard, LockDelay.seconds30);
      expect(LockDelay.values.map((d) => d.name), isNot(contains('never')));
      expect(
          LockDelay.values
              .every((d) => d.duration <= const Duration(minutes: 5)),
          isTrue);
    });

    test('the screen locking locks it, whatever the delay', () async {
      final r = make();
      final lock = await unlockedWith(r, LockDelay.minutes5);
      r.auth.screenLocks();
      await Future<void>.delayed(Duration.zero);
      expect(lock.state, AppLockState.locked);
    });

    test('two minutes without a touch locks it', () async {
      final r = make();
      final lock = await unlockedWith(r, LockDelay.minutes5);
      r.clock.advance(const Duration(minutes: 1, seconds: 59));
      lock.checkIdle();
      expect(lock.state, AppLockState.unlocked);
      r.clock.advance(const Duration(seconds: 1));
      lock.checkIdle();
      expect(lock.state, AppLockState.locked);
    });

    test('a touch starts the two minutes again', () async {
      final r = make();
      final lock = await unlockedWith(r, LockDelay.minutes5);
      r.clock.advance(const Duration(minutes: 1, seconds: 50));
      lock.userActivity();
      r.clock.advance(const Duration(minutes: 1, seconds: 50));
      lock.checkIdle();
      expect(lock.state, AppLockState.unlocked);
    });

    test('locking clears memory through the listeners, even if one fails',
        () async {
      final r = make();
      final lock = await unlockedWith(r, LockDelay.standard);
      var cleared = 0;
      lock.addLockListener(() => throw StateError('boom'));
      lock.addLockListener(() => cleared++);
      final remove = lock.addLockListener(() => cleared += 10);
      remove();
      lock.lock();
      expect(cleared, 1);
    });

    test('locking while the prompt is open locks', () async {
      final r = make();
      r.auth.pending = Completer<BiometricOutcome>();
      final opening = r.lock.unlock();
      await Future<void>.delayed(Duration.zero);
      r.lock.lock();
      expect(r.lock.state, AppLockState.locked);
      r.auth.pending!.complete(BiometricOutcome.success);
      await opening;
      expect(r.lock.state, AppLockState.locked);
    });
  });

  group('a fresh biometric for one action', () {
    test('is refused while locked, without asking', () async {
      final r = make();
      expect(await r.lock.reauthenticate('Approve'), isFalse);
      expect(r.auth.prompts, 0);
    });

    test('succeeds only on a biometric success; a refusal does not lock',
        () async {
      final r = make();
      await r.lock.unlock();
      final before = r.auth.prompts;
      expect(await r.lock.reauthenticate('Approve this action'), isTrue);
      expect(r.auth.prompts, before + 1);
      expect(r.auth.reasons.last, 'Approve this action');
      for (final o in [
        BiometricOutcome.failed,
        BiometricOutcome.cancelled,
        BiometricOutcome.lockout,
      ]) {
        r.auth.outcome = o;
        expect(await r.lock.reauthenticate('Approve'), isFalse);
        expect(r.lock.state, AppLockState.unlocked);
      }
    });

    test('a success after the app locked does not count', () async {
      final r = make();
      await r.lock.unlock();
      r.auth.pending = Completer<BiometricOutcome>();
      final asking = r.lock.reauthenticate('Approve');
      await Future<void>.delayed(Duration.zero);
      r.lock.lock();
      r.auth.pending!.complete(BiometricOutcome.success);
      expect(await asking, isFalse);
    });

    test('the gate used by approvals and settings goes through the lock',
        () async {
      final r = make();
      final gate = LockBiometricGate(r.lock);
      expect(await gate.confirm('Approve this action'), isFalse,
          reason: 'locked');
      await r.lock.unlock();
      expect(await gate.confirm('Approve this action'), isTrue);
      r.auth.outcome = BiometricOutcome.failed;
      expect(await gate.confirm('Approve this action'), isFalse);
    });

    test('a settings change asks the lock for its biometric', () async {
      final r = make();
      await r.lock.unlock();
      final rig = SettingsRig();
      final controller = _controllerWith(rig, LockBiometricGate(r.lock));
      await controller.load();
      final before = r.auth.prompts;
      expect(await controller.apply({'threshold_eur': '500'}), isTrue,
          reason: controller.error);
      expect(r.auth.prompts, before + 1);
      expect(r.auth.reasons.last, 'Change a security setting');
      controller.dispose();
    });
  });

  group('late answers and prompts in flight', () {
    test('a late answer of an earlier prompt is not applied to a newer attempt',
        () async {
      final r = make();
      final first = Completer<BiometricOutcome>();
      r.auth.pending = first;
      final a = r.lock.unlock();
      await Future<void>.delayed(Duration.zero);
      r.lock.lock(); // the screen turned off under prompt A
      final second = Completer<BiometricOutcome>();
      r.auth.pending = second;
      final b = r.lock.unlock();
      await Future<void>.delayed(Duration.zero);
      expect(r.lock.state, AppLockState.unlocking);
      first.complete(BiometricOutcome.success); // stale
      await a;
      expect(r.lock.state, AppLockState.unlocking,
          reason: 'a stale success opens nothing');
      second.complete(BiometricOutcome.failed);
      await b;
      expect(r.lock.state, AppLockState.locked);
    });

    test(
        'leaving for the system prompt does not lock under a re-authentication',
        () async {
      final r = make();
      final lock = await unlockedWith(r, LockDelay.immediately);
      r.auth.pending = Completer<BiometricOutcome>();
      final asking = lock.reauthenticate('Approve this action');
      await Future<void>.delayed(Duration.zero);
      lock.appPaused();
      lock.appResumed();
      expect(lock.state, AppLockState.unlocked);
      r.auth.pending!.complete(BiometricOutcome.success);
      expect(await asking, isTrue);
    });

    test('the last try is forgotten when the app locks', () async {
      final r = make();
      r.auth.outcome = BiometricOutcome.failed;
      await r.lock.unlock();
      expect(r.lock.lastOutcome, BiometricOutcome.failed);
      r.lock.lock();
      expect(r.lock.lastOutcome, isNull);
    });
  });

  group('the lock delay setting', () {
    test('is saved, and read back at the next start', () async {
      final r = make();
      await r.lock.unlock();
      expect(await r.lock.setDelay(LockDelay.minute1), isTrue);
      expect(r.lock.delay, LockDelay.minute1);
      expect(r.store.value, 'minute1');
      final next =
          LockController(authenticator: FakeAuthenticator(), store: r.store);
      addTearDown(next.dispose);
      await next.load();
      expect(next.delay, LockDelay.minute1);
    });

    test('a change needs a fresh biometric, and a refusal changes nothing',
        () async {
      final r = make();
      await r.lock.unlock();
      r.auth.outcome = BiometricOutcome.failed;
      expect(await r.lock.setDelay(LockDelay.minutes5), isFalse);
      expect(r.lock.delay, LockDelay.standard);
      expect(r.store.value, isNull);
    });

    test('an unknown saved value gives 30 seconds, never a longer delay',
        () async {
      final r = make();
      r.store.value = 'never';
      await r.lock.load();
      expect(r.lock.delay, LockDelay.seconds30);
    });

    test('a store that cannot be read gives 30 seconds', () async {
      final r = make();
      r.store.failReads = true;
      await r.lock.load();
      expect(r.lock.delay, LockDelay.seconds30);
    });
  });
}
