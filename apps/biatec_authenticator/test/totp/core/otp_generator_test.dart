import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:crypto/crypto.dart';
import 'package:biatec_authenticator/totp/core/otp_generator.dart';
import 'package:flutter_test/flutter_test.dart';

Uint8List ascii(String s) => Uint8List.fromList(utf8.encode(s));

void main() {
  group('HOTP, RFC 4226 appendix D', () {
    final secret = ascii('12345678901234567890');
    const expected = [
      '755224',
      '287082',
      '359152',
      '969429',
      '338314',
      '254676',
      '287922',
      '162583',
      '399871',
      '520489',
    ];

    for (var i = 0; i < expected.length; i++) {
      test('counter $i is ${expected[i]}', () {
        expect(OtpGenerator.hotp(secret, i), expected[i]);
      });
    }

    test('codes keep their leading zeros', () {
      // Counter 0 of this secret at 8 digits starts with a zero in the RFC 6238 vectors below; here we check the padding itself.
      for (var c = 0; c < 200; c++) {
        expect(OtpGenerator.hotp(secret, c).length, 6);
        expect(OtpGenerator.hotp(secret, c, digits: 8).length, 8);
      }
    });

    test('counters beyond 32 bits work (the counter is 8 bytes)', () {
      final a = OtpGenerator.hotp(secret, 0x1FFFFFFFF);
      final b = OtpGenerator.hotp(secret, 0xFFFFFFFF);
      expect(a, isNot(b));
      expect(a.length, 6);
    });

    test('negative counters and unsupported digit counts are refused', () {
      expect(() => OtpGenerator.hotp(secret, -1), throwsArgumentError);
      expect(
          () => OtpGenerator.hotp(secret, 0, digits: 5), throwsArgumentError);
      expect(
          () => OtpGenerator.hotp(secret, 0, digits: 9), throwsArgumentError);
    });
  });

  group('TOTP, RFC 6238 appendix B (8 digits)', () {
    final secrets = {
      OtpAlgorithm.sha1: ascii('12345678901234567890'),
      OtpAlgorithm.sha256: ascii('12345678901234567890123456789012'),
      OtpAlgorithm.sha512: ascii(
          '1234567890123456789012345678901234567890123456789012345678901234'),
    };
    const times = [
      59,
      1111111109,
      1111111111,
      1234567890,
      2000000000,
      20000000000
    ];
    const expected = {
      OtpAlgorithm.sha1: [
        '94287082',
        '07081804',
        '14050471',
        '89005924',
        '69279037',
        '65353130'
      ],
      OtpAlgorithm.sha256: [
        '46119246',
        '68084774',
        '67062674',
        '91819424',
        '90698825',
        '77737706'
      ],
      OtpAlgorithm.sha512: [
        '90693936',
        '25091201',
        '99943326',
        '93441116',
        '38618901',
        '47863826'
      ],
    };

    for (final algorithm in OtpAlgorithm.values) {
      for (var i = 0; i < times.length; i++) {
        test(
            '${algorithm.uriName} at ${times[i]} s is ${expected[algorithm]![i]}',
            () {
          final account = OtpAccount(
            issuer: 'RFC',
            label: 'vector',
            secret: secrets[algorithm]!,
            algorithm: algorithm,
            digits: 8,
          );
          final now =
              DateTime.fromMillisecondsSinceEpoch(times[i] * 1000, isUtc: true);
          expect(OtpGenerator.code(account, now), expected[algorithm]![i]);
        });
      }
    }
  });

  group('time handling', () {
    final account = OtpAccount(
        issuer: 'x', label: 'y', secret: ascii('12345678901234567890'));
    DateTime at(int s, [int ms = 0]) =>
        DateTime.fromMillisecondsSinceEpoch(s * 1000 + ms, isUtc: true);

    test(
        'the code is the same for the whole period and changes on the boundary',
        () {
      final first = OtpGenerator.code(account, at(60));
      expect(OtpGenerator.code(account, at(89, 999)), first);
      expect(OtpGenerator.code(account, at(90)), isNot(first));
      expect(OtpGenerator.code(account, at(59, 999)), isNot(first));
    });

    test('seconds left count down from the period to 1', () {
      expect(OtpGenerator.secondsLeft(account, at(60)), 30);
      expect(OtpGenerator.secondsLeft(account, at(61)), 29);
      expect(OtpGenerator.secondsLeft(account, at(89)), 1);
      expect(OtpGenerator.secondsLeft(account, at(90)), 30);
    });

    test('the fraction left goes from 1 to 0 within a period', () {
      expect(OtpGenerator.fractionLeft(account, at(60)), 1.0);
      expect(OtpGenerator.fractionLeft(account, at(75)), closeTo(0.5, 1e-9));
      expect(OtpGenerator.fractionLeft(account, at(89, 999)), lessThan(0.001));
    });

    test('the next code is the code of the following period', () {
      expect(OtpGenerator.nextCode(account, at(75)),
          OtpGenerator.code(account, at(90)));
    });

    test('works at the epoch and with other periods', () {
      expect(OtpGenerator.step(account, at(0)), 0);
      final sixty = OtpAccount(
          issuer: '',
          label: 'a',
          secret: ascii('12345678901234567890'),
          period: 60);
      expect(OtpGenerator.step(sixty, at(119)), 1);
      expect(OtpGenerator.secondsLeft(sixty, at(119)), 1);
    });

    test('a clock before 1970 gives step 0 instead of failing', () {
      final before = DateTime.fromMillisecondsSinceEpoch(-5000, isUtc: true);
      expect(OtpGenerator.step(account, before), 0);
      expect(OtpGenerator.code(account, before),
          OtpGenerator.code(account, at(0)));
      // The countdown agrees with the code: it counts from 1970, not from the bad clock.
      expect(OtpGenerator.secondsLeft(account, before), 30);
      expect(OtpGenerator.fractionLeft(account, before), 1.0);
    });

    test('the zone of the clock does not matter', () {
      final utc = DateTime.utc(2026, 10, 28, 12, 0, 5);
      final local = utc.toLocal();
      expect(
          OtpGenerator.code(account, local), OtpGenerator.code(account, utc));
    });

    test('a counter-based account ignores time and uses its counter', () {
      final h = OtpAccount(
          issuer: '',
          label: 'h',
          secret: ascii('12345678901234567890'),
          type: OtpType.hotp,
          counter: 3);
      expect(OtpGenerator.code(h, at(0)), '969429');
      expect(OtpGenerator.code(h, at(99999)), '969429');
      // Its next code is not shown ahead of time.
      expect(() => OtpGenerator.nextCode(h, at(0)), throwsStateError);
    });
  });

  group('display', () {
    test('groups codes in threes for reading, as the spec asks', () {
      expect(OtpGenerator.group('123456'), '123 456');
      expect(OtpGenerator.group('12345678'), '123 456 78');
      expect(OtpGenerator.group('1234567'), '123 456 7');
    });

    test('a counter above 32 bits is written in full', () {
      // RFC 4226 big-endian counter: the high half must not be lost.
      final k = Uint8List.fromList(List.filled(20, 1));
      final mac = Hmac(sha1, k).convert([0, 0, 0, 1, 0, 0, 0, 1]).bytes;
      final o = mac[19] & 0x0F;
      final want = (((mac[o] & 0x7F) << 24) |
              (mac[o + 1] << 16) |
              (mac[o + 2] << 8) |
              mac[o + 3]) %
          1000000;
      expect(
          OtpGenerator.hotp(k, 0x100000001), want.toString().padLeft(6, '0'));
    });
  });
}
