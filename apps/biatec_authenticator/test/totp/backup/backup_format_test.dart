import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/backup/argon2.dart';
import 'package:biatec_authenticator/totp/backup/backup_format.dart';
import 'package:biatec_authenticator/totp/backup/passphrase.dart';
import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:flutter_test/flutter_test.dart';

/// A policy cheap enough for the many tests; the few tests that matter for the floor use the real one.
const cheap = Argon2idParams(memoryKiB: 64, iterations: 1, lanes: 1);
const cheapPolicy = BackupPolicy(floor: cheap);

final calls = <Argon2idParams>[];

Future<Uint8List> inline(
    Uint8List password, Uint8List salt, Argon2idParams p) async {
  calls.add(p);
  return argon2id(password, salt, p);
}

BackupCodec codec({BackupPolicy policy = cheapPolicy}) =>
    BackupCodec(policy: policy, kdf: inline);

OtpAccount account(String issuer, String label, int seed,
        {OtpType type = OtpType.totp,
        OtpAlgorithm algorithm = OtpAlgorithm.sha1,
        int digits = 6,
        int period = 30,
        int counter = 0}) =>
    OtpAccount(
      issuer: issuer,
      label: label,
      secret:
          Uint8List.fromList(List.generate(20, (i) => seed * 13 + i * 7 + 5)),
      type: type,
      algorithm: algorithm,
      digits: digits,
      period: type == OtpType.hotp ? 30 : period,
      counter: counter,
    );

const pass = 'correct horse battery staple';

bool hasBytes(Uint8List haystack, List<int> needle) {
  outer:
  for (var i = 0; i + needle.length <= haystack.length; i++) {
    for (var j = 0; j < needle.length; j++) {
      if (haystack[i + j] != needle[j]) continue outer;
    }
    return true;
  }
  return false;
}

Future<String> failureOf(Future<Object?> run) async {
  try {
    await run;
  } on BackupFailure catch (e) {
    return e.toString();
  }
  return 'NO FAILURE';
}

void main() {
  setUp(calls.clear);

  group('round trip', () {
    test('every account comes back exactly', () async {
      final accounts = [
        account('GitHub', 'alice@example.com', 1),
        account('Bank', 'bob', 2,
            algorithm: OtpAlgorithm.sha512, digits: 8, period: 60),
        account('', 'no issuer', 3,
            algorithm: OtpAlgorithm.sha256, digits: 7, period: 15),
        account('Counter', 'hotp', 4, type: OtpType.hotp, counter: 12345),
        account('Zürich ünï', 'ключ 密钥', 5),
      ];
      final file = await codec().encrypt(accounts, pass);
      final back = await codec().decrypt(file, pass);
      expect(back.length, accounts.length);
      for (var i = 0; i < accounts.length; i++) {
        final a = accounts[i];
        final b = back[i];
        expect(b.issuer, a.issuer);
        expect(b.label, a.label);
        expect(b.copySecret(), a.copySecret());
        expect(b.algorithm, a.algorithm);
        expect(b.digits, a.digits);
        expect(b.period, a.period);
        expect(b.type, a.type);
        expect(b.counter, a.counter);
        b.wipe();
      }
    });

    test('an empty backup is a valid backup', () async {
      final file = await codec().encrypt([], pass);
      expect(await codec().decrypt(file, pass), isEmpty);
    });

    test('at the real floor, with the real parameters in the header', () async {
      final real = BackupCodec(kdf: inline);
      final file = await real.encrypt([account('A', 'b', 1)], pass);
      final h = ByteData.sublistView(file);
      expect(h.getUint32(6), 64 * 1024, reason: 'memory in KiB');
      expect(h.getUint32(10), 3);
      expect(h.getUint8(14), 4);
      expect(calls.single,
          const Argon2idParams(memoryKiB: 65536, iterations: 3, lanes: 4));
      final back = await real.decrypt(file, pass);
      expect(back.single.issuer, 'A');
    });

    test('the default floor is the one in the spec', () {
      const floor = BackupPolicy();
      expect(floor.floor,
          const Argon2idParams(memoryKiB: 65536, iterations: 3, lanes: 4));
    });
  });

  group('what the file shows', () {
    test('no plaintext issuer, label or secret, in any usual spelling',
        () async {
      final a = account('DistinctiveIssuerName', 'secret.person@x.org', 7);
      final secret = a.copySecret();
      final file = await codec().encrypt([a], pass);
      expect(hasBytes(file, utf8.encode('DistinctiveIssuerName')), isFalse);
      expect(hasBytes(file, utf8.encode('secret.person')), isFalse);
      expect(hasBytes(file, secret), isFalse);
      expect(hasBytes(file, utf8.encode(Base32.encode(secret))), isFalse);
      expect(hasBytes(file, utf8.encode(base64.encode(secret))), isFalse);
      expect(hasBytes(file, utf8.encode('"accounts"')), isFalse);
      expect(hasBytes(file, utf8.encode(pass)), isFalse);
    });

    test('the file name says what and when, never whose', () {
      final name = BackupCodec.fileName(DateTime(2026, 11, 6, 9));
      expect(name, 'biatec-authenticator-backup-20261106.biatecbackup');
    });

    test('the size does not say how many accounts there are', () async {
      final one = await codec().encrypt([account('A', 'a', 1)], pass);
      final five = await codec().encrypt(
          [for (var i = 0; i < 5; i++) account('A$i', 'a', i + 1)], pass);
      expect(five.length, one.length);
      expect((one.length - BackupCodec.headerLength - 16) % 4096, 0);
    });

    test('two files of the same data differ: a new salt and nonce every time',
        () async {
      final a = [account('A', 'a', 1)];
      final f1 = await codec().encrypt(a, pass);
      final f2 = await codec().encrypt(a, pass);
      expect(f1, isNot(f2));
      expect(f1.sublist(15, 43), isNot(f2.sublist(15, 43)));
    });
  });

  group('every failure is the same failure', () {
    late Uint8List good;

    setUp(() async {
      good = await codec()
          .encrypt([account('A', 'a', 1), account('B', 'b', 2)], pass);
    });

    test('a wrong passphrase', () async {
      expect(
          await failureOf(codec().decrypt(good, 'wrong horse battery staple')),
          BackupFailure.message);
      expect(await failureOf(codec().decrypt(good, '')), BackupFailure.message);
      expect(await failureOf(codec().decrypt(good, '$pass ')),
          BackupFailure.message);
    });

    test('a changed bit in any header byte', () async {
      for (var i = 0; i < BackupCodec.headerLength; i++) {
        final bad = Uint8List.fromList(good)..[i] ^= 0x01;
        expect(
            await failureOf(codec().decrypt(bad, pass)), BackupFailure.message,
            reason: 'header byte $i');
      }
    });

    test('a changed bit in the ciphertext and in the tag', () async {
      final positions = [
        BackupCodec.headerLength,
        BackupCodec.headerLength + 1,
        BackupCodec.headerLength + 2000,
        good.length - 17,
        good.length - 16,
        good.length - 1,
      ];
      for (final i in positions) {
        for (final bit in [0x01, 0x80]) {
          final bad = Uint8List.fromList(good)..[i] ^= bit;
          expect(await failureOf(codec().decrypt(bad, pass)),
              BackupFailure.message,
              reason: 'byte $i bit $bit');
        }
      }
    });

    test('a truncated file, a lengthened one, and nothing', () async {
      for (final n in [
        0,
        1,
        4,
        42,
        43,
        58,
        59,
        100,
        good.length - 1,
        good.length - 16
      ]) {
        expect(await failureOf(codec().decrypt(good.sublist(0, n), pass)),
            BackupFailure.message,
            reason: 'cut to $n');
      }
      expect(
          await failureOf(
              codec().decrypt(Uint8List.fromList([...good, 0]), pass)),
          BackupFailure.message);
      expect(await failureOf(codec().decrypt(Uint8List(0), pass)),
          BackupFailure.message);
    });

    test('garbage, and a file of another kind', () async {
      expect(
          await failureOf(
              codec().decrypt(Uint8List.fromList(List.filled(500, 7)), pass)),
          BackupFailure.message);
      expect(
          await failureOf(codec().decrypt(
              Uint8List.fromList(utf8.encode('{"accounts":[]}')), pass)),
          BackupFailure.message);
    });

    test('a file over the size limit', () async {
      final big = Uint8List(5 * 1024 * 1024);
      expect(
          await failureOf(codec().decrypt(big, pass)), BackupFailure.message);
    });

    test('an unknown version or key derivation', () async {
      for (final (pos, value) in [(4, 2), (4, 0), (5, 2), (5, 0)]) {
        final bad = Uint8List.fromList(good)..[pos] = value;
        expect(
            await failureOf(codec().decrypt(bad, pass)), BackupFailure.message);
      }
    });
  });

  group('the floor', () {
    test('a file made with weaker parameters is refused, with the same failure',
        () async {
      final weak = await codec()
          .encrypt([account('A', 'a', 1)], pass); // made at the cheap floor
      // Opened by a codec whose floor is higher: the weak file is a downgrade.
      final strict = BackupCodec(
          policy: const BackupPolicy(
              floor: Argon2idParams(memoryKiB: 256, iterations: 2, lanes: 2)),
          kdf: inline);
      expect(
          await failureOf(strict.decrypt(weak, pass)), BackupFailure.message);
    });

    test('each parameter alone below the floor is refused', () async {
      const floor = Argon2idParams(memoryKiB: 256, iterations: 2, lanes: 2);
      final strict =
          BackupCodec(policy: const BackupPolicy(floor: floor), kdf: inline);
      final maker = BackupCodec(policy: cheapPolicy, kdf: inline);
      for (final p in [
        const Argon2idParams(memoryKiB: 128, iterations: 2, lanes: 2),
        const Argon2idParams(memoryKiB: 256, iterations: 1, lanes: 2),
        const Argon2idParams(memoryKiB: 256, iterations: 2, lanes: 1),
      ]) {
        final file =
            await maker.sealJson({'v': 1, 'accounts': []}, pass, params: p);
        expect(
            await failureOf(strict.decrypt(file, pass)), BackupFailure.message,
            reason: '$p');
      }
      final ok =
          await maker.sealJson({'v': 1, 'accounts': []}, pass, params: floor);
      expect(await strict.decrypt(ok, pass), isEmpty,
          reason: 'exactly the floor is fine');
    });

    test(
        'a refused file still costs a real attempt, so it is not answered faster',
        () async {
      final weak = await codec().encrypt([account('A', 'a', 1)], pass);
      final strict = BackupCodec(
          policy: const BackupPolicy(
              floor: Argon2idParams(memoryKiB: 256, iterations: 2, lanes: 2)),
          kdf: inline);
      calls.clear();
      await failureOf(strict.decrypt(weak, pass));
      expect(calls,
          [const Argon2idParams(memoryKiB: 256, iterations: 2, lanes: 2)],
          reason: 'one derivation at the floor, as for a wrong passphrase');
      calls.clear();
      await failureOf(strict.decrypt(Uint8List(10), pass));
      expect(calls, hasLength(1));
    });

    test('parameters above the ceiling are refused without being run',
        () async {
      final greedy = Uint8List.fromList(await codec().encrypt([], pass));
      ByteData.sublistView(greedy).setUint32(6, 0xFFFFFFFF); // 4 TiB of memory
      calls.clear();
      expect(await failureOf(codec().decrypt(greedy, pass)),
          BackupFailure.message);
      expect(calls.every((p) => p.memoryKiB <= 64), isTrue,
          reason: 'only a derivation at the floor ran');
    });

    test('the app never writes below its floor', () async {
      final file = await BackupCodec(
              policy: const BackupPolicy(
                  floor:
                      Argon2idParams(memoryKiB: 128, iterations: 2, lanes: 2)),
              kdf: inline)
          .encrypt([], pass);
      final h = ByteData.sublistView(file);
      expect([h.getUint32(6), h.getUint32(10), h.getUint8(14)], [128, 2, 2]);
    });
  });

  group('a poisoned file gets no further than a bad QR code', () {
    Future<Object?> open(Object json) async =>
        codec().decrypt(await codec().sealJson(json, pass), pass);

    test('accounts that break the account rules', () async {
      final base = {
        'issuer': 'A',
        'label': 'a',
        'secret': Base32.encode(List.generate(20, (i) => i + 1)),
        'algorithm': 'SHA1',
        'digits': 6,
        'period': 30,
        'type': 'totp',
        'counter': 0,
      };
      expect(
          await open({
            'v': 1,
            'accounts': [base]
          }),
          hasLength(1));
      for (final bad in [
        {...base, 'secret': 'GEZDGNBV'}, // weak
        {...base, 'secret': '1!1'}, // not Base32
        {...base, 'algorithm': 'MD5'},
        {...base, 'digits': 12},
        {...base, 'period': 7},
        {...base, 'type': 'steam'},
        {...base, 'label': ''},
        {...base, 'label': 'evil${String.fromCharCode(0x202E)}name'},
        {...base, 'counter': 5}, // a counter on a time-based account
        {...base, 'digits': '6'},
        {...base}..remove('secret'),
      ]) {
        expect(
            await failureOf(open({
              'v': 1,
              'accounts': [base, bad]
            })),
            BackupFailure.message,
            reason: '$bad');
      }
    });

    test('the wrong shape, version or size', () async {
      for (final bad in [
        {'v': 2, 'accounts': []},
        {'accounts': []},
        {'v': 1},
        {'v': 1, 'accounts': 'x'},
        {
          'v': 1,
          'accounts': [5]
        },
        [1, 2],
        'text',
      ]) {
        expect(await failureOf(open(bad)), BackupFailure.message,
            reason: '$bad');
      }
      final many = [
        for (var i = 0; i < 1001; i++)
          {
            'issuer': 'A',
            'label': 'a$i',
            'secret': Base32.encode(List.generate(20, (k) => i + k + 1)),
            'algorithm': 'SHA1',
            'digits': 6,
            'period': 30,
            'type': 'totp',
            'counter': 0,
          }
      ];
      expect(await failureOf(open({'v': 1, 'accounts': many})),
          BackupFailure.message,
          reason: 'more accounts than any real phone has');
    });
  });

  group('passphrase rules', () {
    test('short is refused, and says how long', () {
      final v = Passphrase.check('short one');
      expect(v.accepted, isFalse);
      expect(v.strength, PassphraseStrength.refused);
      expect(v.problem, contains('12'));
    });

    test('the weakest long ones are refused', () {
      for (final weak in [
        'passwordpassword',
        'password1234',
        '111111111111',
        'aaaaaaaaaaaaaaaa',
        'abcabcabcabcabc',
        'qwertyuiop12',
        '123456789012',
        'abcdefghijkl',
        'iloveyou12345',
        'biatecbiatecbiatec',
        'Authenticator1!',
        'letmeinletmein',
      ]) {
        final v = Passphrase.check(weak);
        expect(v.accepted, isFalse, reason: '$weak is ${v.bits} bits');
      }
    });

    test('a phrase of unrelated words and a long mixed one are accepted', () {
      for (final good in [
        'correct horse battery staple',
        'plum-ladder-orbit-fabric-22',
        'Tr0ub4dor&3 purple elephant',
        'my cat drinks tea at noon',
      ]) {
        final v = Passphrase.check(good);
        expect(v.accepted, isTrue,
            reason: '$good is ${v.bits} bits: ${v.problem}');
      }
    });

    test('the strength grows with the phrase', () {
      final fair = Passphrase.check('wolf tiger lemon sled');
      final strong = Passphrase.check(
          'wolf tiger lemon sled harbor quartz nickel violin tundra');
      expect(strong.bits, greaterThan(fair.bits));
      expect(strong.strength, PassphraseStrength.strong);
    });

    test('the confirmation must match', () {
      expect(
          Passphrase.check('correct horse battery staple',
                  confirmation: 'correct horse battery stapl')
              .accepted,
          isFalse);
      expect(
          Passphrase.check('correct horse battery staple',
                  confirmation: 'correct horse battery staple')
              .accepted,
          isTrue);
    });

    test('unicode counts by characters, not bytes', () {
      expect(Passphrase.check('ключключключ').accepted, isFalse,
          reason: '12 characters but repeated');
      expect(Passphrase.check('Zürich Käse Äpfel Öl 7').accepted, isTrue);
    });
  });
}
