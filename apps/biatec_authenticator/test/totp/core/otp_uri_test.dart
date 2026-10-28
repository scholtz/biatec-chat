import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_uri.dart';
import 'package:flutter_test/flutter_test.dart';

OtpUriError errorOf(String link) {
  try {
    OtpUri.parse(link);
  } on OtpUriException catch (e) {
    return e.error;
  }
  fail('expected a refusal: $link');
}

void main() {
  group('Base32 (RFC 4648)', () {
    test('test vectors', () {
      const vectors = {
        'f': 'MY',
        'fo': 'MZXQ',
        'foo': 'MZXW6',
        'foob': 'MZXW6YQ',
        'fooba': 'MZXW6YTB',
        'foobar': 'MZXW6YTBOI',
      };
      vectors.forEach((plain, encoded) {
        expect(Base32.encode(utf8.encode(plain)), encoded);
        expect(utf8.decode(Base32.decode(encoded)), plain);
      });
    });

    test('tolerates case, spaces, dashes and padding as people paste them', () {
      final want = utf8.encode('foobar');
      expect(Base32.decode('mzxw 6ytb oi'), want);
      expect(Base32.decode('MZXW-6YTB-OI'), want);
      expect(Base32.decode('MZXW6YTBOI======'), want);
      expect(Base32.decode('  MZXW6YQ=  '), utf8.encode('foob'));
    });

    test('refuses anything else, without echoing the input', () {
      for (final bad in [
        '',
        '   ',
        '=',
        'MZXW1',
        'MZXW6YT!',
        'MZ=XW',
        'M',
        'MZX',
        'MZXW6Y',
        'MZ XW6YTBOJ'
      ]) {
        try {
          Base32.decode(bad);
          fail('should refuse "$bad"');
        } on Base32Exception catch (e) {
          if (bad.length > 3) expect(e.message, isNot(contains(bad)));
        }
      }
    });

    test('non-canonical trailing bits are refused', () {
      // "MZ" decodes to one byte; "MX" has the same first byte but nonzero leftover bits.
      expect(() => Base32.decode('MX'), throwsA(isA<Base32Exception>()));
    });

    test('round trip for every length', () {
      for (var n = 1; n < 70; n++) {
        final bytes =
            Uint8List.fromList(List.generate(n, (i) => (i * 37 + n) & 0xFF));
        expect(Base32.decode(Base32.encode(bytes)), bytes, reason: 'length $n');
      }
    });
  });

  group('parsing links real services emit', () {
    test('Google style: issuer in the label and the parameter', () {
      final a = OtpUri.parse(
          'otpauth://totp/Example:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example');
      expect(a.issuer, 'Example');
      expect(a.label, 'alice@example.com');
      expect(a.type, OtpType.totp);
      expect(a.algorithm, OtpAlgorithm.sha1);
      expect(a.digits, 6);
      expect(a.period, 30);
      expect(a.secret, Base32.decode('JBSWY3DPEHPK3PXP'));
    });

    test('GitHub style: issuer only in the label, encoded colon', () {
      final a = OtpUri.parse(
          'otpauth://totp/GitHub%3Aoctocat?secret=ABCDEFGHIJKLMNOP&issuer=GitHub');
      expect(a.issuer, 'GitHub');
      expect(a.label, 'octocat');
    });

    test('no issuer at all', () {
      final a =
          OtpUri.parse('otpauth://totp/just-a-name?secret=JBSWY3DPEHPK3PXP');
      expect(a.issuer, '');
      expect(a.label, 'just-a-name');
    });

    test('Microsoft style with an encoded space and SHA-256, 8 digits, 60 s',
        () {
      final a = OtpUri.parse(
          'otpauth://totp/Contoso%20Ltd:bob?secret=JBSWY3DPEHPK3PXP&algorithm=SHA256&digits=8&period=60&issuer=Contoso%20Ltd');
      expect(a.issuer, 'Contoso Ltd');
      expect(a.algorithm, OtpAlgorithm.sha256);
      expect(a.digits, 8);
      expect(a.period, 60);
    });

    test('Biatec style, lower case algorithm and scheme', () {
      final a = OtpUri.parse(
          'OTPAUTH://TOTP/Biatec:me@biatec.io?secret=jbswy3dpehpk3pxp&algorithm=sha512&issuer=Biatec');
      expect(a.issuer, 'Biatec');
      expect(a.algorithm, OtpAlgorithm.sha512);
    });

    test('counter-based account', () {
      final a = OtpUri.parse(
          'otpauth://hotp/Bank:carol?secret=JBSWY3DPEHPK3PXP&counter=7&issuer=Bank');
      expect(a.type, OtpType.hotp);
      expect(a.counter, 7);
    });

    test(
        'the issuer parameter wins over the label prefix; extra parameters are ignored',
        () {
      final a = OtpUri.parse(
          'otpauth://totp/Old:me?secret=JBSWY3DPEHPK3PXP&issuer=New&image=https%3A%2F%2Fx%2Fi.png');
      expect(a.issuer, 'New');
      expect(a.label, 'me');
    });

    test('surrounding whitespace is ignored', () {
      expect(OtpUri.parse('  otpauth://totp/a?secret=JBSWY3DPEHPK3PXP\n').label,
          'a');
    });
  });

  group('refusing links', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';

    test('not a setup link', () {
      expect(errorOf('https://example.com/?secret=JBSWY3DPEHPK3PXP'),
          OtpUriError.notOtpAuth);
      expect(errorOf('hello'), OtpUriError.notOtpAuth);
      expect(errorOf(''), OtpUriError.notOtpAuth);
    });

    test('unsupported type', () {
      expect(errorOf('otpauth://steam/x?$s'), OtpUriError.unsupportedType);
      expect(errorOf('otpauth://motp/x?$s'), OtpUriError.unsupportedType);
    });

    test('too long', () {
      expect(errorOf('otpauth://totp/${'a' * 3000}?$s'), OtpUriError.tooLong);
    });

    test('secret problems', () {
      expect(errorOf('otpauth://totp/x'), OtpUriError.missingSecret);
      expect(errorOf('otpauth://totp/x?secret='), OtpUriError.missingSecret);
      expect(errorOf('otpauth://totp/x?secret=not*base32'),
          OtpUriError.invalidSecret);
      expect(errorOf('otpauth://totp/x?secret=MZX'), OtpUriError.invalidSecret);
    });

    test('algorithm', () {
      expect(errorOf('otpauth://totp/x?$s&algorithm=MD5'),
          OtpUriError.invalidAlgorithm);
      expect(errorOf('otpauth://totp/x?$s&algorithm='),
          OtpUriError.invalidAlgorithm);
    });

    test('digits', () {
      for (final d in [
        '5',
        '9',
        '0',
        '-6',
        '6.0',
        '+6',
        'six',
        '',
        '66666666666666666666'
      ]) {
        expect(
            errorOf('otpauth://totp/x?$s&digits=$d'), OtpUriError.invalidDigits,
            reason: d);
      }
    });

    test('period', () {
      for (final p in ['14', '121', '0', '-30', '30.5', 'x', '']) {
        expect(
            errorOf('otpauth://totp/x?$s&period=$p'), OtpUriError.invalidPeriod,
            reason: p);
      }
    });

    test('counters', () {
      expect(errorOf('otpauth://hotp/x?$s'), OtpUriError.missingCounter);
      expect(errorOf('otpauth://hotp/x?$s&counter=-1'),
          OtpUriError.invalidCounter);
      expect(errorOf('otpauth://hotp/x?$s&counter=abc'),
          OtpUriError.invalidCounter);
      expect(errorOf('otpauth://totp/x?$s&counter=1'),
          OtpUriError.unexpectedCounter);
    });

    test('a setting given twice', () {
      expect(errorOf('otpauth://totp/x?$s&secret=MZXW6YTBOI'),
          OtpUriError.duplicateParameter);
      expect(errorOf('otpauth://totp/x?$s&digits=6&digits=8'),
          OtpUriError.duplicateParameter);
      expect(errorOf('otpauth://totp/x?$s&algorithm=SHA1&algorithm=SHA512'),
          OtpUriError.duplicateParameter);
    });

    test('names that could spoof another account', () {
      expect(errorOf('otpauth://totp/x%0Ay?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/Bank%E2%80%AEmoc.x?$s'),
          OtpUriError.invalidName);
      expect(
          errorOf('otpauth://totp/x?$s&issuer=a%00b'), OtpUriError.invalidName);
      expect(
          errorOf('otpauth://totp/${'n' * 300}?$s'), OtpUriError.invalidName);
      // A stray percent sign is read as itself, not as an error.
      expect(OtpUri.parse('otpauth://totp/%ZZ?$s').label, '%ZZ');
    });

    test('messages never contain the link or the secret', () {
      for (final bad in [
        'otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&digits=99',
        'otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&algorithm=MD5',
        'otpauth://hotp/x?secret=JBSWY3DPEHPK3PXP',
        'otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&secret=MZXW6YTBOI',
      ]) {
        try {
          OtpUri.parse(bad);
          fail('should refuse $bad');
        } on OtpUriException catch (e) {
          expect(e.message, isNot(contains('JBSWY3DPEHPK3PXP')));
          expect(e.toString(), isNot(contains('JBSWY3DPEHPK3PXP')));
        }
      }
    });
  });

  group('writing and the round trip', () {
    OtpAccount account({
      String issuer = 'Example Co',
      String label = 'alice+test@example.com',
      OtpType type = OtpType.totp,
      OtpAlgorithm algorithm = OtpAlgorithm.sha256,
      int digits = 8,
      int period = 45,
      int counter = 0,
    }) =>
        OtpAccount(
          issuer: issuer,
          label: label,
          secret: Uint8List.fromList(List.generate(20, (i) => i * 11 + 3)),
          algorithm: algorithm,
          digits: digits,
          period: period,
          type: type,
          counter: counter,
        );

    test('time-based, with spaces and a plus sign in the names', () {
      final a = account();
      final b = OtpUri.parse(OtpUri.write(a));
      expect(b.sameAccountAs(a), isTrue);
      expect(b.issuer, 'Example Co');
      expect(b.label, 'alice+test@example.com');
    });

    test('counter-based keeps its counter', () {
      final a = account(type: OtpType.hotp, counter: 42, period: 30);
      final b = OtpUri.parse(OtpUri.write(a));
      expect(b.type, OtpType.hotp);
      expect(b.counter, 42);
      expect(b.sameAccountAs(a), isTrue);
    });

    test('special characters and non-Latin names survive', () {
      for (final issuer in [
        'A&B: Bank',
        'Café Ñandú',
        '日本の銀行',
        'a=b?c#d',
        ''
      ]) {
        final a = account(issuer: issuer, label: 'ユーザー:x y/z');
        final b = OtpUri.parse(OtpUri.write(a));
        expect(b.issuer, issuer, reason: issuer);
        expect(b.label, 'ユーザー:x y/z'.trim(), reason: issuer);
      }
    });

    test('a colon in the label after an issuer is kept', () {
      final a = account(issuer: 'Svc', label: 'a:b');
      expect(OtpUri.parse(OtpUri.write(a)).label, 'a:b');
    });
  });

  group('the account model', () {
    Uint8List key() => Uint8List.fromList(List.filled(20, 7));

    test('toString never contains the secret', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key());
      final text = a.toString();
      expect(text, contains('I'));
      expect(text, isNot(contains(Base32.encode(a.secret))));
      expect(text, isNot(contains('7, 7')));
    });

    test('invalid parameters are refused', () {
      expect(() => OtpAccount(issuer: '', label: 'a', secret: Uint8List(0)),
          throwsArgumentError);
      expect(() => OtpAccount(issuer: '', label: 'a', secret: key(), digits: 5),
          throwsArgumentError);
      expect(
          () => OtpAccount(issuer: '', label: 'a', secret: key(), period: 10),
          throwsArgumentError);
      expect(
          () => OtpAccount(issuer: '', label: 'a', secret: key(), counter: -1),
          throwsArgumentError);
      expect(() => OtpAccount(issuer: 'x' * 300, label: 'a', secret: key()),
          throwsArgumentError);
    });

    test('wipe overwrites the secret in memory', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      a.wipe();
      expect(a.secret.every((b) => b == 0), isTrue);
    });

    test(
        'sameAccountAs compares the secret and parameters, not the counter or id',
        () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key(), id: '1');
      final b = a.copyWith(id: '2', counter: 9);
      expect(a.sameAccountAs(b), isTrue);
      final other = Uint8List.fromList(key())..[19] = 8;
      expect(
          a.sameAccountAs(OtpAccount(issuer: 'I', label: 'L', secret: other)),
          isFalse);
      expect(a.sameAccountAs(a.copyWith(label: 'M')), isFalse);
    });

    test('secret strength is reported in bits', () {
      expect(OtpAccount(issuer: '', label: 'a', secret: key()).secretBits, 160);
    });
  });
}
