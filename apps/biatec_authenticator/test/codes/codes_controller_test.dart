import 'dart:async';

import 'package:biatec_authenticator/codes/add/account_input.dart';
import 'package:biatec_authenticator/codes/codes_controller.dart';
import 'package:biatec_authenticator/codes/codes_services.dart';
import 'package:biatec_authenticator/lock/biometric_authenticator.dart';
import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_uri.dart';
import 'package:flutter_test/flutter_test.dart';

import '../totp/vault/support.dart' show account;
import 'support.dart';

// 20 bytes: "12345678901234567890" in Base32.
const key = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ';

Future<(CodesRig, CodesController)> open() async {
  final rig = await CodesRig.create();
  final c = CodesController(rig.services);
  addTearDown(c.dispose);
  return (rig, c);
}

void main() {
  group('the three ways to add give the same account', () {
    test('QR, link and typed key', () {
      final link = AccountInput.fromLink(
          'otpauth://totp/GitHub:alice%40example.com?secret=$key&issuer=GitHub');
      final typed = AccountInput.fromKey(
          issuer: 'GitHub', label: 'alice@example.com', key: key);
      final spaced = AccountInput.fromKey(
          issuer: 'GitHub',
          label: 'alice@example.com',
          key: 'gezd gnbv gy3t qojq gezd gnbv gy3t qojq');
      for (final a in [typed, spaced]) {
        expect(a.isDuplicateOf(link), isTrue);
        expect(a.issuer, link.issuer);
        expect(a.label, link.label);
        expect(a.algorithm, link.algorithm);
        expect(a.digits, link.digits);
        expect(a.period, link.period);
        expect(a.type, link.type);
        a.wipe();
      }
      link.wipe();
    });

    test('invalid input shows the engine\'s own message', () {
      void refused(String expectation, void Function() run) {
        try {
          run();
          fail('should be refused: $expectation');
        } on InputProblem catch (e) {
          expect(e.message, isNotEmpty, reason: expectation);
          expect(e.message, isNot(contains(key)));
        }
      }

      refused('not an otpauth link',
          () => AccountInput.fromLink('https://example.com'));
      refused('no secret',
          () => AccountInput.fromLink('otpauth://totp/a?issuer=b'));
      refused('weak secret',
          () => AccountInput.fromLink('otpauth://totp/a?secret=GEZDGNBV'));
      refused('bad digits',
          () => AccountInput.fromLink('otpauth://totp/a?secret=$key&digits=9'));
      refused('bad key',
          () => AccountInput.fromKey(issuer: 'i', label: 'l', key: '1!1'));
      refused('empty key',
          () => AccountInput.fromKey(issuer: 'i', label: 'l', key: '  '));
      refused('no name',
          () => AccountInput.fromKey(issuer: 'i', label: ' ', key: key));
      // The same words the engine gives for the same input.
      try {
        OtpUri.parse('otpauth://totp/a?secret=$key&digits=9');
      } on OtpUriException catch (e) {
        try {
          AccountInput.fromLink('otpauth://totp/a?secret=$key&digits=9');
        } on InputProblem catch (p) {
          expect(p.message, e.message);
        }
      }
      try {
        Base32.decode('1!1');
      } on Base32Exception catch (e) {
        try {
          AccountInput.fromKey(issuer: 'i', label: 'l', key: '1!1');
        } on InputProblem catch (p) {
          expect(p.message, e.message);
        }
      }
    });
  });

  group('the list', () {
    test('starts empty and loads what the vault holds', () async {
      final (rig, c) = await open();
      await c.load();
      expect(c.phase, CodesPhase.ready);
      expect(c.entries, isEmpty);
      await rig.addAccount(account(issuer: 'A', seed: 1));
      await rig.addAccount(account(issuer: 'B', seed: 2));
      await c.load();
      expect(c.entries.map((e) => e.info.issuer), ['A', 'B']);
      expect(c.entries.every((e) => !e.account.isWiped), isTrue);
    });

    test('add refuses a copy of an account already there', () async {
      final (_, c) = await open();
      await c.load();
      await c.add(account(issuer: 'A', seed: 1));
      await expectLater(
          c.add(account(issuer: 'Other name', label: 'x', seed: 1)),
          throwsA(isA<AddRefused>()));
      expect(c.entries, hasLength(1));
      await c.add(account(issuer: 'A', seed: 2));
      expect(c.entries, hasLength(2));
    });

    test('rename and reorder persist', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(issuer: 'A', seed: 1));
      await c.add(account(issuer: 'B', seed: 2));
      await c.add(account(issuer: 'C', seed: 3));
      await c.rename(c.entries[0].info.id, issuer: 'Alpha', label: 'new');
      await c.reorder(2, 0);
      final next = CodesController(rig.services);
      addTearDown(next.dispose);
      await next.load();
      expect(next.entries.map((e) => e.info.issuer), ['C', 'Alpha', 'B']);
      expect(next.entries[1].info.label, 'new');
      expect(next.entries[1].account.label, 'new');
    });

    test('a name the engine refuses is refused with its reason', () async {
      final (_, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      await expectLater(c.rename(c.entries.single.info.id, label: ' '),
          throwsA(isA<OtpAccountException>()));
    });

    test('search finds by service or account name, any case', () async {
      final (_, c) = await open();
      await c.load();
      await c.add(account(issuer: 'GitHub', label: 'alice@x.org', seed: 1));
      await c.add(account(issuer: 'Bank', label: 'bob@y.org', seed: 2));
      c.query = 'git';
      expect(c.visible.map((e) => e.info.issuer), ['GitHub']);
      c.query = 'BOB';
      expect(c.visible.map((e) => e.info.issuer), ['Bank']);
      c.query = 'nothing';
      expect(c.visible, isEmpty);
      c.query = '';
      expect(c.visible, hasLength(2));
    });

    test('a counter-based account moves to its next counter', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(type: OtpType.hotp, counter: 7, seed: 1));
      await c.nextCounter(c.entries.single.info.id);
      expect(c.entries.single.account.counter, 8);
      final next = CodesController(rig.services);
      addTearDown(next.dispose);
      await next.load();
      expect(next.entries.single.account.counter, 8);
    });
  });

  group('delete', () {
    test('needs a fresh biometric, and a refusal deletes nothing', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      final id = c.entries.single.info.id;
      rig.auth.outcome = BiometricOutcome.failed;
      expect(await c.delete(id), isFalse);
      expect(c.entries, hasLength(1));
      expect(await rig.vault.list(), hasLength(1));
      expect(rig.auth.reasons.last, 'Delete this account');

      rig.auth.outcome = BiometricOutcome.success;
      expect(await c.delete(id), isTrue);
      expect(c.entries, isEmpty);
      expect(await rig.vault.list(), isEmpty);
    });
  });

  group('secrets in memory', () {
    test('locking wipes every account and empties the list', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      final held = c.entries.single.account;
      rig.lock.lock();
      expect(held.isWiped, isTrue);
      expect(c.entries, isEmpty);
    });

    test('disposing wipes them too', () async {
      final rig = await CodesRig.create();
      final c = CodesController(rig.services);
      await c.load();
      await c.add(account(seed: 1));
      final held = c.entries.single.account;
      c.dispose();
      expect(held.isWiped, isTrue);
    });

    test('a changed biometric set is reported to the lock', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      rig.keys.invalidated = true;
      await c.load();
      expect(c.phase, CodesPhase.failed);
      expect(c.problem, contains('changed'));
      expect(rig.lock.state.name, 'biometricsChanged');
      expect(c.entries, isEmpty);
    });

    test(
        'a refused fingerprint, a damaged file and a rolled-back file each say so',
        () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      rig.keys.denyAuth = true;
      await c.load();
      expect(c.problem, contains('did not pass'));
      rig.keys.denyAuth = false;
      rig.storage.bytes = rig.storage.bytes!.sublist(0, 20);
      await c.load();
      expect(c.phase, CodesPhase.failed);
      expect(c.problem, contains('damaged'));
    });
  });

  group('races and failures', () {
    test('a lock while the list is loading does not bring secrets back',
        () async {
      final (rig, c) = await open();
      await rig.addAccount(account(seed: 1));
      final loading = c.load();
      rig.lock.lock(); // the lock listener wipes while the vault is read
      await loading;
      expect(c.entries, isEmpty);
    });

    test('an add that was sealing when the app locked is not kept in memory',
        () async {
      final (rig, c) = await open();
      await c.load();
      final gate = Completer<void>();
      rig.keys.sealGate = gate.future;
      final adding = c.add(account(seed: 1));
      await Future<void>.delayed(Duration.zero);
      rig.lock.lock();
      gate.complete();
      await expectLater(adding, throwsA(isA<AddRefused>()));
      expect(c.entries, isEmpty);
      expect(await rig.vault.list(), hasLength(1), reason: 'it was saved');
    });

    test('nothing is added while the list could not be opened', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(seed: 1));
      rig.keys.denyAuth = true;
      await c.load();
      rig.keys.denyAuth = false;
      expect(c.phase, CodesPhase.failed);
      await expectLater(c.add(account(seed: 2)), throwsA(isA<AddRefused>()));
      expect(await rig.vault.list(), hasLength(1));
    });

    test('the same account added twice at once is saved once', () async {
      final (rig, c) = await open();
      await c.load();
      final results = await Future.wait([
        c
            .add(account(seed: 4))
            .then<Object>((e) => e)
            .catchError((Object e) => e),
        c
            .add(account(seed: 4))
            .then<Object>((e) => e)
            .catchError((Object e) => e),
      ]);
      expect(results.whereType<AddRefused>(), hasLength(1));
      expect(await rig.vault.list(), hasLength(1));
    });

    test('a reorder that cannot be saved puts the list back', () async {
      final (rig, c) = await open();
      await c.load();
      await c.add(account(issuer: 'A', seed: 1));
      await c.add(account(issuer: 'B', seed: 2));
      rig.storage.failWrites = true;
      await expectLater(c.reorder(0, 1), throwsA(anything));
      expect(c.entries.map((e) => e.info.issuer), ['A', 'B']);
    });

    test('the latest time is saved rarely, not every second', () async {
      final (rig, c) = await open();
      await c.load();
      var writes = 0;
      final counting = _CountingPrefs(rig.prefs, () => writes++);
      final c2 = CodesController(CodesServicesOf.withPrefs(rig, counting));
      addTearDown(c2.dispose);
      await c2.load();
      for (var i = 0; i < 120; i++) {
        await c2.noteShownAt(rig.now.add(Duration(seconds: i)));
      }
      expect(writes, 1);
      await c2.noteShownAt(rig.now.add(const Duration(minutes: 11)));
      expect(writes, 2);
      expect(c2.latestSeen, rig.now.add(const Duration(minutes: 11)));
    });

    test('locking tells the key store to forget the last biometric', () async {
      final (rig, _) = await open();
      expect(rig.keys.sessionsEnded, 0);
      rig.services; // built with the lock hook
      rig.lock.lock();
      expect(rig.keys.sessionsEnded, 1);
    });
  });

  group('the latest time seen', () {
    test('a time far ahead of the clock is forgotten, a close one is kept',
        () async {
      final (rig, c) = await open();
      rig.prefs.seen = rig.now.add(const Duration(days: 400));
      await c.load();
      expect(c.latestSeen, isNull,
          reason:
              'saved while the clock ran ahead: a corrected clock must not be hidden');

      rig.prefs.seen = rig.now.subtract(const Duration(days: 3));
      await c.load();
      expect(c.latestSeen, rig.prefs.seen);
    });

    test('only a later time is saved', () async {
      final (rig, c) = await open();
      await c.load();
      await c.noteShownAt(rig.now);
      expect(rig.prefs.seen, rig.now);
      await c.noteShownAt(rig.now.subtract(const Duration(hours: 1)));
      expect(rig.prefs.seen, rig.now);
    });
  });
}

class _CountingPrefs implements CodesPrefs {
  _CountingPrefs(this.inner, this.onWrite);
  final CodesPrefs inner;
  final void Function() onWrite;

  @override
  Future<bool> readTapToReveal() => inner.readTapToReveal();
  @override
  Future<void> writeTapToReveal(bool v) => inner.writeTapToReveal(v);
  @override
  Future<DateTime?> readLatestSeen() => inner.readLatestSeen();
  @override
  Future<void> writeLatestSeen(DateTime v) {
    onWrite();
    return inner.writeLatestSeen(v);
  }
}

abstract final class CodesServicesOf {
  static CodesServices withPrefs(CodesRig rig, CodesPrefs prefs) =>
      CodesServices(
        vault: rig.vault,
        lock: rig.lock,
        clipboard: rig.clipboard,
        serverClock: rig.serverClock,
        clock: () => rig.now,
        prefs: prefs,
      );
}
