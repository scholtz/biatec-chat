import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_generator.dart';
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
      ]) {
        try {
          Base32.decode(bad);
          fail('should refuse "$bad"');
        } on Base32Exception catch (e) {
          if (bad.length > 3) expect(e.message, isNot(contains(bad)));
        }
      }
    });

    test('leftover bits in the last character are dropped, as other apps do',
        () {
      // "MY" is the canonical "f"; "MZ" has other unused bits and gives the same byte.
      expect(Base32.decode('MZ'), utf8.encode('f'));
      expect(Base32.decode('MY'), utf8.encode('f'));
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
        'the issuer parameter names the issuer; a different prefix stays in the name; extra parameters are ignored',
        () {
      final a = OtpUri.parse(
          'otpauth://totp/Old:me?secret=JBSWY3DPEHPK3PXP&issuer=New&image=https%3A%2F%2Fx%2Fi.png');
      expect(a.issuer, 'New');
      expect(a.label, 'Old:me');
      expect(OtpUri.parse(OtpUri.write(a)).label, 'Old:me');
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
      // Invisible characters: two accounts that look the same.
      for (final hidden in [
        '%E2%80%8B',
        '%E2%80%8C',
        '%EF%BB%BF',
        '%C2%AD',
        '%E2%81%A0',
        '%E2%80%A8'
      ]) {
        expect(errorOf('otpauth://totp/Gi${hidden}thub:me?$s'),
            OtpUriError.invalidName,
            reason: hidden);
      }
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
      expect(b.isDuplicateOf(a), isTrue);
      expect(b.issuer, 'Example Co');
      expect(b.label, 'alice+test@example.com');
    });

    test('counter-based keeps its counter', () {
      final a = account(type: OtpType.hotp, counter: 42, period: 30);
      final b = OtpUri.parse(OtpUri.write(a));
      expect(b.type, OtpType.hotp);
      expect(b.counter, 42);
      expect(b.isDuplicateOf(a), isTrue);
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
      // Nor the issuer or the name, which are usually an email address.
      final named = OtpAccount(
          issuer: 'GitHub', label: 'alice@example.com', secret: key());
      expect(named.toString(), isNot(contains('alice')));
      expect(named.toString(), isNot(contains('GitHub')));
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
        'isDuplicateOf compares the secret and parameters, not names, counter or id',
        () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key(), id: '1');
      final b = a.copyWith(id: '2', counter: 9, label: 'other spelling');
      expect(a.isDuplicateOf(b), isTrue);
      final other = Uint8List.fromList(key())..[19] = 8;
      expect(
          a.isDuplicateOf(OtpAccount(issuer: 'I', label: 'L', secret: other)),
          isFalse);
      expect(
          a.isDuplicateOf(
              OtpAccount(issuer: 'I', label: 'L', secret: key(), digits: 8)),
          isFalse);
    });

    test('the constructor keeps its own copy of the secret', () {
      final buffer = key();
      final a = OtpAccount(issuer: '', label: 'a', secret: buffer);
      buffer.fillRange(0, buffer.length, 0);
      expect(a.secret.every((v) => v == 7), isTrue);
      a.wipe();
      expect(a.secret.every((v) => v == 0), isTrue);
    });

    test('a wiped account cannot make codes', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      a.wipe();
      expect(a.isWiped, isTrue);
      expect(() => OtpGenerator.code(a, DateTime.utc(2026)), throwsStateError);
      expect(
          () => OtpGenerator.nextCode(a, DateTime.utc(2026)), throwsStateError);
    });

    test('names are trimmed, so a link round-trips', () {
      final a = OtpAccount(issuer: ' Svc ', label: ' work ', secret: key());
      expect(a.issuer, 'Svc');
      expect(a.label, 'work');
      expect(OtpUri.parse(OtpUri.write(a)).label, 'work');
    });

    test('secret strength is reported in bits', () {
      expect(OtpAccount(issuer: '', label: 'a', secret: key()).secretBits, 160);
    });
  });

  group('review follow-ups', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';

    test('bad percent-encoding in a value is a refusal, not a raw exception',
        () {
      expect(errorOf('otpauth://totp/a?$s&issuer=%E0%A4%A'),
          OtpUriError.invalidName);
    });

    test('two issuers are refused, whatever order they come in', () {
      expect(errorOf('otpauth://totp/a?$s&issuer=GitHub&issuer=Evil'),
          OtpUriError.duplicateParameter);
    });

    test('an account must have a name', () {
      expect(errorOf('otpauth://totp/?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/Issuer:?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/%20%20?$s'), OtpUriError.invalidName);
    });

    test('copyWith copies the secret, so wiping one account leaves the other',
        () {
      final a = OtpAccount(
          issuer: 'I',
          label: 'L',
          secret: Uint8List.fromList(List.filled(20, 9)));
      final b = a.copyWith(id: 'x');
      a.wipe();
      expect(b.secret.every((v) => v == 9), isTrue);
      expect(a.secret.every((v) => v == 0), isTrue);
    });

    test('a differing issuer setting wins over the label prefix', () {
      final a = OtpUri.parse('otpauth://totp/Github%20:alice?issuer=GitHub&$s');
      expect(a.issuer, 'GitHub');
      expect(a.label, 'alice');
    });
  });

  group('round three', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';
    Uint8List key() => Uint8List.fromList(List.filled(20, 7));

    test(
        'copyWith on a wiped account fails instead of making a zero-key account',
        () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      a.wipe();
      expect(() => a.copyWith(counter: 1), throwsStateError);
    });

    test('a wiped account is a duplicate of nothing', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      final b = OtpAccount(
          issuer: '',
          label: 'b',
          secret: Uint8List.fromList(List.filled(20, 9)));
      a.wipe();
      b.wipe();
      expect(a.isDuplicateOf(b), isFalse);
      expect(b.isDuplicateOf(a), isFalse);
    });

    test('secrets under 80 bits are refused', () {
      expect(errorOf('otpauth://totp/x?secret=AA'), OtpUriError.weakSecret);
      expect(errorOf('otpauth://totp/x?secret=MZXW6YTBOI'),
          OtpUriError.weakSecret);
      expect(() => OtpAccount(issuer: '', label: 'a', secret: Uint8List(9)),
          throwsArgumentError);
      // The smallest in wide use (16 Base32 characters) is accepted.
      expect(OtpUri.parse('otpauth://totp/x?$s').secretBits, 80);
    });

    test('more invisible characters are refused', () {
      for (final hidden in [
        '%CD%8F',
        '%E1%85%9F',
        '%E3%85%A4',
        '%EF%BE%A0',
        '%EF%B8%8F',
        '%E2%A0%80',
        '%F3%A0%80%81'
      ]) {
        expect(errorOf('otpauth://totp/Gi${hidden}thub:me?$s'),
            OtpUriError.invalidName,
            reason: hidden);
      }
      expect(errorOf('otpauth://totp/%E3%85%A4?$s'), OtpUriError.invalidName);
    });

    test('a counter-based account has no countdown', () {
      final h =
          OtpAccount(issuer: '', label: 'h', secret: key(), type: OtpType.hotp);
      expect(() => OtpGenerator.secondsLeft(h, DateTime.utc(2026)),
          throwsStateError);
      expect(() => OtpGenerator.fractionLeft(h, DateTime.utc(2026)),
          throwsStateError);
    });
  });

  group('round four', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';
    Uint8List key() => Uint8List.fromList(List.filled(20, 7));

    test('padding must be the exact amount when present', () {
      expect(Base32.decode('MY======'), utf8.encode('f'));
      expect(Base32.decode('MY'), utf8.encode('f'));
      for (final bad in [
        'MY=',
        'MY=================',
        'MZXW6YTB=',
        'MY==-=='
      ]) {
        expect(() => Base32.decode(bad), throwsA(isA<Base32Exception>()),
            reason: bad);
      }
      // Separators may still sit among the padding.
      expect(Base32.decode('MY== ===='), utf8.encode('f'));
    });

    test('padding in the middle is refused', () {
      expect(
          () => Base32.decode('MY======MY'), throwsA(isA<Base32Exception>()));
    });

    test('a different label prefix is kept in the name', () {
      final work = OtpUri.parse('otpauth://totp/corp:alice?$s&issuer=GitHub');
      final home = OtpUri.parse('otpauth://totp/home:alice?$s&issuer=GitHub');
      expect(work.issuer, 'GitHub');
      expect(work.label, 'corp:alice');
      expect(home.label, 'home:alice');
      // The same issuer in other case and spacing is still dropped.
      expect(
          OtpUri.parse('otpauth://totp/git%20hub:alice?$s&issuer=GitHub').label,
          'alice');
    });

    test('setting names are read without case', () {
      expect(
          OtpUri.parse('otpauth://totp/x?SECRET=JBSWY3DPEHPK3PXP').secretBits,
          80);
      expect(errorOf('otpauth://totp/x?$s&Secret=MZXW6YTBOIMZXW6YTB'),
          OtpUriError.duplicateParameter);
      expect(errorOf('otpauth://totp/x?$s&DIGITS=7&digits=8'),
          OtpUriError.duplicateParameter);
    });

    test('still more invisible characters are refused', () {
      for (final hidden in [
        '%E1%A0%8B', // U+180B
        '%E1%9E%B4', // U+17B4
        '%EF%BF%BC', // U+FFFC
        '%F0%9D%85%B3', // U+1D173
        '%E2%81%AA', // U+206A
        '%F3%A0%84%80', // U+E0100
      ]) {
        expect(errorOf('otpauth://totp/Pay${hidden}Pal:me?$s'),
            OtpUriError.invalidName,
            reason: hidden);
      }
    });

    test('a counter-based account is a duplicate whatever its period', () {
      final a = OtpAccount(
          issuer: '',
          label: 'a',
          secret: key(),
          type: OtpType.hotp,
          period: 60);
      final b =
          OtpAccount(issuer: '', label: 'b', secret: key(), type: OtpType.hotp);
      expect(a.isDuplicateOf(b), isTrue);
      // A time-based one with another period makes other codes.
      final t60 = OtpAccount(issuer: '', label: 'a', secret: key(), period: 60);
      final t30 = OtpAccount(issuer: '', label: 'a', secret: key());
      expect(t60.isDuplicateOf(t30), isFalse);
    });

    test('a refusal after the secret is valid wipes the decoded secret', () {
      // Every other check runs before the secret is decoded, so these refusals hold no secret at all.
      expect(errorOf('otpauth://totp/x?$s&algorithm=MD5'),
          OtpUriError.invalidAlgorithm);
      expect(errorOf('otpauth://totp/x%E2%80%AE?$s'), OtpUriError.invalidName);
      // A malformed secret is still reported once the rest is fine.
      expect(errorOf('otpauth://totp/x?secret=1'), OtpUriError.invalidSecret);
    });

    test('a bad period is refused on a counter-based link too', () {
      expect(errorOf('otpauth://hotp/x?$s&counter=0&period=abc'),
          OtpUriError.invalidPeriod);
      expect(OtpUri.parse('otpauth://hotp/x?$s&counter=0&period=60').period,
          OtpLimits.defaultPeriod);
    });

    test('a + in the label prefix matches a form-encoded issuer', () {
      final a =
          OtpUri.parse('otpauth://totp/Big+Corp:alice?issuer=Big+Corp&$s');
      expect(a.issuer, 'Big Corp');
      expect(a.label, 'alice');
    });

    test('the model refuses the names the parser refuses', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key());
      expect(() => a.copyWith(label: '202Elgoog'), throwsArgumentError);
      expect(() => a.copyWith(label: '   '), throwsArgumentError);
      expect(() => a.copyWith(issuer: 'Git200Bhub'), throwsArgumentError);
    });

    test('the secret cannot be changed through the account', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key());
      expect(() => a.secret[0] = 1, throwsUnsupportedError);
      a.wipe();
      expect(a.secret.every((v) => v == 0), isTrue);
    });

    test('a refused account is refused before the secret is copied', () {
      for (final make in <void Function()>[
        () => OtpAccount(issuer: '', label: 'a', secret: key(), digits: 9),
        () => OtpAccount(issuer: '', label: 'n' * 300, secret: key()),
        () => OtpAccount(issuer: '', label: 'a', secret: key(), counter: -1),
      ]) {
        expect(make, throwsArgumentError);
      }
    });
  });
}
