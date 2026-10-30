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
      expect(
          errorOf('otpauth://totp/x?secret=MZXW1'), OtpUriError.invalidSecret);
      // Only padding, dashes or spaces: there is no key at all.
      for (final blank in ['====', '----', '%C2%A0%C2%A0', '%20']) {
        expect(errorOf('otpauth://totp/x?secret=$blank'),
            OtpUriError.missingSecret,
            reason: blank);
      }
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

  group('review follow-ups', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';

    test('bad percent-encoding in a value is a refusal, not a raw exception',
        () {
      expect(errorOf('otpauth://totp/a?$s&issuer=%E0%A4%A'),
          OtpUriError.invalidEncoding);
    });

    test('two issuers are refused, whatever order they come in', () {
      expect(errorOf('otpauth://totp/a?$s&issuer=GitHub&issuer=Evil'),
          OtpUriError.duplicateParameter);
    });

    test('an account must have a name, or an issuer to use as one', () {
      expect(errorOf('otpauth://totp/?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/%20%20?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/:?$s'), OtpUriError.invalidName);
      // With an issuer, the issuer is the name, as other apps do.
      expect(OtpUri.parse('otpauth://totp/Issuer:?$s').label, 'Issuer');
      final g = OtpUri.parse('otpauth://totp/?$s&issuer=GitHub');
      expect(g.issuer, 'GitHub');
      expect(g.label, 'GitHub');
    });

    test('copyWith copies the secret, so wiping one account leaves the other',
        () {
      final a = OtpAccount(
          issuer: 'I',
          label: 'L',
          secret: Uint8List.fromList(List.generate(20, (i) => 9 + i)));
      final b = a.copyWith(id: 'x');
      final view = a.secret;
      a.wipe();
      expect(b.secret, List.generate(20, (i) => 9 + i));
      expect(view.every((b) => b == 0), isTrue);
    });

    test('a differing issuer setting wins over the label prefix', () {
      final a = OtpUri.parse('otpauth://totp/Github%20:alice?issuer=GitHub&$s');
      expect(a.issuer, 'GitHub');
      expect(a.label, 'alice');
    });
  });

  group('round three', () {
    const s = 'secret=JBSWY3DPEHPK3PXP';
    Uint8List key() => Uint8List.fromList(List.generate(20, (i) => 7 + i));

    test(
        'copyWith on a wiped account fails instead of making a zero-key account',
        () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      a.wipe();
      expect(() => a.copyWith(counter: 1), throwsStateError);
    });

    test('a line break or tab at the end of a name is refused, not trimmed',
        () {
      for (final bad in ['GitHub\n', '\talice', 'a\r']) {
        expect(() => OtpAccount(issuer: '', label: bad, secret: key()),
            throwsArgumentError,
            reason: bad);
      }
      final ls = String.fromCharCode(0x2028);
      expect(() => OtpAccount(issuer: 'x$ls', label: 'a', secret: key()),
          throwsArgumentError);
      expect(errorOf('otpauth://totp/alice%0A?$s'), OtpUriError.invalidName);
      expect(errorOf('otpauth://totp/alice?$s&issuer=Evil%0D'),
          OtpUriError.invalidName);
      // Plain spaces are still trimmed.
      expect(OtpAccount(issuer: ' I ', label: ' a ', secret: key()).label, 'a');
    });

    test('the Ogham space mark is kept, not turned into a space', () {
      final ogham = String.fromCharCode(0x1680);
      expect(OtpAccount(issuer: '', label: 'a${ogham}b', secret: key()).label,
          'a${ogham}b');
    });

    test('Egyptian hieroglyph letters are not refused as format controls', () {
      expect(
          OtpLimits.hasUnsafeCharacters(String.fromCharCode(0x13441)), isFalse);
      expect(
          OtpLimits.hasUnsafeCharacters(String.fromCharCode(0x13430)), isTrue);
    });

    test('a clock reset to before the last time seen is caught', () {
      final seen = DateTime.utc(2028);
      expect(
          OtpGenerator.clockIsPlausible(DateTime.utc(2026, 1, 2),
              notBefore: seen),
          isFalse);
      expect(
          OtpGenerator.clockIsPlausible(DateTime.utc(2028, 2), notBefore: seen),
          isTrue);
    });

    test('a duplicate check with a wiped account fails closed', () {
      // Answering "no" would let the same key be stored twice; it throws so the caller reloads.
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      final b = OtpAccount(issuer: '', label: 'b', secret: key());
      a.wipe();
      expect(() => a.isDuplicateOf(b), throwsStateError);
      expect(() => b.isDuplicateOf(a), throwsStateError);
    });

    test('a wiped account does not hand out its zeros as the secret', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      final view = a.secret;
      a.wipe();
      expect(() => a.secret, throwsStateError);
      expect(view.every((b) => b == 0), isTrue);
    });

    test('copyWith refuses a new id together with clearId', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key(), id: '1');
      expect(() => a.copyWith(id: '2', clearId: true), throwsArgumentError);
    });

    test('a link without "//" is not a setup link', () {
      expect(errorOf('otpauth:x?y=://'), OtpUriError.notOtpAuth);
      expect(errorOf('otpauth:totp/a?secret=JBSWY3DPEHPK3PXP'),
          OtpUriError.notOtpAuth);
    });

    test('one idea of whitespace: Base32 skips it, names match without it', () {
      final ff = String.fromCharCode(0x0C);
      final nel = String.fromCharCode(0x85);
      expect(Base32.decode('JBSW${ff}Y3DP${nel}EHPK3PXP'),
          Base32.decode('JBSWY3DPEHPK3PXP'));
      // A line break in a name is still refused, not turned into a space.
      expect(() => OtpAccount(issuer: '', label: 'a\nb', secret: key()),
          throwsArgumentError);
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
    Uint8List key() => Uint8List.fromList(List.generate(20, (i) => 7 + i));

    test('a copy of the secret survives a later wipe, the view does not', () {
      final a = OtpAccount(issuer: '', label: 'a', secret: key());
      final view = a.secret;
      final copy = a.copySecret();
      a.wipe();
      expect(view.every((b) => b == 0), isTrue);
      expect(copy, key());
      expect(a.copySecret, throwsStateError);
    });

    test('a key of one repeated byte is weak', () {
      expect(errorOf('otpauth://totp/x?secret=AAAAAAAAAAAAAAAA'),
          OtpUriError.weakSecret);
      expect(
          () => OtpAccount(
              issuer: '',
              label: 'a',
              secret: Uint8List.fromList(List.filled(20, 7))),
          throwsA(isA<OtpAccountException>()));
    });

    test('parser and account agree on what an empty name is', () {
      // A no-break space as the name: the issuer becomes the name.
      expect(
          OtpUri.parse('otpauth://totp/GitHub:%C2%A0?issuer=GitHub&$s').label,
          'GitHub');
      // A no-break space as the issuer is no issuer: the path names it.
      final a = OtpUri.parse('otpauth://totp/Foo:alice?issuer=%C2%A0&$s');
      expect(a.issuer, 'Foo');
      expect(a.label, 'alice');
    });

    test('a clock a little behind the last time seen is still plausible', () {
      final seen = DateTime.utc(2028, 5, 2);
      expect(
          OtpGenerator.clockIsPlausible(DateTime.utc(2028, 5, 1),
              notBefore: seen),
          isTrue);
    });

    test('a link with an empty type is not a setup link', () {
      expect(errorOf('otpauth:///GitHub:alice?$s'), OtpUriError.notOtpAuth);
    });

    test('hotp refuses a zeroed or one-byte key, like the account', () {
      expect(() => OtpGenerator.hotp(Uint8List(20), 0),
          throwsA(isA<OtpAccountException>()));
      expect(() => OtpGenerator.hotp(Uint8List.fromList(List.filled(20, 5)), 0),
          throwsA(isA<OtpAccountException>()));
    });

    test('algorithm names are ASCII only', () {
      final longS = String.fromCharCode(0x017F);
      expect(
          errorOf(
              'otpauth://totp/x?$s&algorithm=${Uri.encodeQueryComponent('${longS}ha1')}'),
          OtpUriError.invalidAlgorithm);
    });

    test('keys typeset with en dashes, em dashes or minus signs are read', () {
      final want = Base32.decode('JBSWY3DPEHPK3PXP');
      for (final d in [0x2012, 0x2013, 0x2014, 0x2212]) {
        final dash = String.fromCharCode(d);
        expect(Base32.decode('JBSW${dash}Y3DP${dash}EHPK${dash}3PXP'), want,
            reason: d.toRadixString(16));
      }
    });

    test('hyphenated algorithm names are read', () {
      expect(OtpUri.parse('otpauth://totp/x?$s&algorithm=SHA-256').algorithm,
          OtpAlgorithm.sha256);
    });

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
      // Padding is one run: a separator inside it is refused.
      expect(() => Base32.decode('MY== ===='), throwsA(isA<Base32Exception>()));
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

    test('a counter-based account has no period, so it cannot differ by one',
        () {
      // A period on a counter-based account would be lost in a link, so it is refused.
      expect(
          () => OtpAccount(
              issuer: '',
              label: 'a',
              secret: key(),
              type: OtpType.hotp,
              period: 60),
          throwsA(isA<OtpAccountException>()
              .having((e) => e.problem, 'problem', OtpAccountProblem.period)));
      final a =
          OtpAccount(issuer: '', label: 'a', secret: key(), type: OtpType.hotp);
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
      final rlo = String.fromCharCode(0x202E); // right-to-left override
      final zwsp = String.fromCharCode(0x200B); // zero width space
      expect(() => a.copyWith(label: '${rlo}lgoog'), throwsArgumentError);
      expect(() => a.copyWith(label: '   '), throwsArgumentError);
      expect(() => a.copyWith(issuer: 'Git${zwsp}hub'), throwsArgumentError);
    });

    test('the secret cannot be changed through the account', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key());
      expect(() => a.secret[0] = 1, throwsUnsupportedError);
      final view = a.secret;
      a.wipe();
      expect(view.every((b) => b == 0), isTrue);
    });

    test('a wiped account cannot be written as a link', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key());
      a.wipe();
      expect(() => OtpUri.write(a), throwsStateError);
    });

    test('the remaining format characters and lone surrogates are refused', () {
      for (final u in [
        0x0605,
        0x06DD,
        0x070F,
        0x08E2,
        0x110BD,
        0x110CD,
        0x13430,
        0xD800
      ]) {
        expect(OtpLimits.hasUnsafeCharacters('Git${String.fromCharCode(u)}Hub'),
            isTrue,
            reason: u.toRadixString(16));
      }
      expect(errorOf('otpauth://totp/Git%D8%85Hub:me?$s'),
          OtpUriError.invalidName);
    });

    test('keys laid out with no-break or thin spaces are accepted', () {
      final want = Base32.decode('JBSWY3DPEHPK3PXP');
      final nbsp = String.fromCharCode(0x00A0);
      final thin = String.fromCharCode(0x2009);
      expect(Base32.decode('JBSW${nbsp}Y3DP${thin}EHPK 3PXP'), want);
    });

    test('broken encoding in a setting is its own error', () {
      expect(errorOf('otpauth://totp/x?secret=JBSW%FF%FE'),
          OtpUriError.invalidEncoding);
    });

    test('dot segments in the label are kept as written', () {
      expect(OtpUri.parse('otpauth://totp/corp/../alice?$s').label,
          'corp/../alice');
      expect(OtpUri.parse('otpauth://totp/team/./bob?$s').label, 'team/./bob');
    });

    test('a plus in the label stays a plus (plus-addressed email)', () {
      final a = OtpUri.parse('otpauth://totp/Svc:a+b@example.com?$s');
      expect(a.label, 'a+b@example.com');
      expect(OtpUri.parse(OtpUri.write(a)).label, 'a+b@example.com');
    });

    test('hotp refuses a key under the account floor', () {
      expect(() => OtpGenerator.hotp(Uint8List(9), 0), throwsArgumentError);
      expect(() => OtpGenerator.hotp(Uint8List(0), 0), throwsArgumentError);
    });

    test('copyWith can drop the vault id', () {
      final a = OtpAccount(issuer: 'I', label: 'L', secret: key(), id: '1');
      expect(a.copyWith(clearId: true).id, isNull);
      expect(a.copyWith().id, '1');
    });

    test('limit messages follow the limits', () {
      expect(OtpLimits.digitsMessage, contains('${OtpLimits.maxDigits}'));
      expect(OtpLimits.periodMessage, contains('${OtpLimits.maxPeriod}'));
    });

    test('raw non-ASCII names, as real QR codes carry them, are read', () {
      final a = OtpUri.parse('otpauth://totp/Café:alice?$s&issuer=Café');
      expect(a.issuer, 'Café');
      expect(a.label, 'alice');
      final b = OtpUri.parse('otpauth://totp/ユーザー:x y?$s');
      expect(b.issuer, 'ユーザー');
      expect(b.label, 'x y');
      // Real escapes still decode, and a stray percent is still itself.
      expect(OtpUri.parse('otpauth://totp/%41b?$s').label, 'Ab');
      expect(OtpUri.parse('otpauth://totp/a%ZZé?$s').label, 'a%ZZé');
    });

    test('an issuer with a colon matches without case', () {
      final a =
          OtpUri.parse('otpauth://totp/ACME:Corp:alice?$s&issuer=acme:corp');
      expect(a.issuer, 'acme:corp');
      expect(a.label, 'alice');
    });

    test('a counter-based account has no time step', () {
      final h =
          OtpAccount(issuer: '', label: 'h', secret: key(), type: OtpType.hotp);
      expect(() => OtpGenerator.step(h, DateTime.utc(2026)), throwsStateError);
    });

    test('a time-based account cannot carry a counter', () {
      final t = OtpAccount(issuer: '', label: 't', secret: key());
      expect(() => t.copyWith(counter: 5), throwsArgumentError);
      expect(
          () => OtpAccount(issuer: '', label: 't', secret: key(), counter: 1),
          throwsArgumentError);
    });

    test('a plus in the issuer is a real character', () {
      // "C:" names another issuer than "C++", so the prefix stays.
      final a = OtpUri.parse('otpauth://totp/C:alice?$s&issuer=C%2B%2B');
      expect(a.issuer, 'C++');
      expect(a.label, 'C:alice');
      expect(
          OtpUri.parse('otpauth://totp/C%2B%2B:alice?$s&issuer=C%2B%2B').label,
          'alice');
    });

    test('a key too short for one byte is weak, not invalid', () {
      expect(errorOf('otpauth://totp/x?secret=A'), OtpUriError.weakSecret);
      expect(
          errorOf('otpauth://totp/x?secret=AAAAAAAA'), OtpUriError.weakSecret);
    });

    test('a wrong character is named before a padding problem', () {
      try {
        Base32.decode('ABCDEFG1=');
        fail('should refuse');
      } on Base32Exception catch (e) {
        expect(e.message, contains('not part of Base32'));
      }
    });

    test('overlays, private use and noncharacters are refused in names', () {
      for (final u in [0x0338, 0xE000, 0xF0000, 0xFDD0, 0xFFFE, 0x1FFFF]) {
        expect(OtpLimits.hasUnsafeCharacters('GitHub${String.fromCharCode(u)}'),
            isTrue,
            reason: u.toRadixString(16));
      }
      expect(OtpLimits.hasUnsafeCharacters('Café ユーザー'), isFalse);
    });

    test('a huge link is refused before it is copied', () {
      expect(errorOf(' ' * 5000), OtpUriError.tooLong);
    });

    test('hotp refuses with the account rule it broke', () {
      expect(
          () => OtpGenerator.hotp(Uint8List(5), 0),
          throwsA(isA<OtpAccountException>().having(
              (e) => e.problem, 'problem', OtpAccountProblem.weakSecret)));
    });

    test('an escaped plus is a plus, only a literal one may be a space', () {
      // "A%2BB" names "A+B", which is not the issuer "A B": the prefix stays.
      final a = OtpUri.parse('otpauth://totp/A%2BB:alice?$s&issuer=A%20B');
      expect(a.label, 'A+B:alice');
      // A literal "+" in the path may mean a space.
      expect(OtpUri.parse('otpauth://totp/A+B:alice?$s&issuer=A%20B').label,
          'alice');
      // Without an issuer setting the path is read as written.
      expect(
          OtpUri.parse('otpauth://totp/Big+Corp:alice?$s').issuer, 'Big+Corp');
    });

    test('user info or a port in the link is refused', () {
      expect(errorOf('otpauth://GitHub@totp/alice?$s'), OtpUriError.notOtpAuth);
      expect(errorOf('otpauth://totp:80/alice?$s'), OtpUriError.notOtpAuth);
    });

    test('padding broken up by spaces is refused', () {
      expect(() => Base32.decode('MFRGGZDFMFRGGZDFME== =='),
          throwsA(isA<Base32Exception>()));
      // Spaces after the padding are fine.
      expect(Base32.decode('MY======  '), utf8.encode('f'));
    });

    test('a leading colon with an issuer setting means no issuer prefix', () {
      final a = OtpUri.parse('otpauth://totp/:alice@x.com?$s&issuer=Acme');
      expect(a.issuer, 'Acme');
      expect(a.label, 'alice@x.com');
      expect(OtpUri.parse('otpauth://totp/:?$s&issuer=Acme').label, 'Acme');
    });

    test('an unpaired surrogate in the raw label is refused', () {
      final lone = String.fromCharCode(0xD800);
      expect(errorOf('otpauth://totp/a${lone}b?$s'), OtpUriError.invalidName);
    });

    test('zero-padded numbers in range are read', () {
      final h = OtpUri.parse(
          'otpauth://hotp/x?$s&counter=000000000000000000001&digits=0008');
      expect(h.counter, 1);
      expect(h.digits, 8);
    });

    test('counter=0 on a time-based link is harmless', () {
      expect(
          OtpUri.parse('otpauth://totp/x?$s&counter=0&period=30').counter, 0);
      expect(OtpUri.parse('otpauth://totp/x?$s&counter=00').counter, 0);
    });

    test('a wrong character is named before misplaced padding', () {
      try {
        Base32.decode('JBSW=0');
        fail('should refuse');
      } on Base32Exception catch (e) {
        expect(e.message, contains('not part of Base32'));
      }
    });

    test('digit and period ranges come from the account', () {
      expect(
          errorOf('otpauth://totp/x?$s&digits=9'), OtpUriError.invalidDigits);
      expect(
          errorOf('otpauth://totp/x?$s&period=5'), OtpUriError.invalidPeriod);
      expect(errorOf('otpauth://hotp/x?$s&counter=1&period=5'),
          OtpUriError.invalidPeriod);
    });

    test('every account written as a link reads back the same', () {
      for (final a in [
        OtpAccount(issuer: 'I', label: 'L', secret: key(), period: 60),
        OtpAccount(
            issuer: 'I',
            label: 'L',
            secret: key(),
            type: OtpType.hotp,
            counter: 7),
      ]) {
        final b = OtpUri.parse(OtpUri.write(a));
        expect((b.type, b.period, b.counter, b.digits),
            (a.type, a.period, a.counter, a.digits));
      }
    });

    test('a malformed counter on a time-based link is invalid, not unexpected',
        () {
      expect(
          errorOf('otpauth://totp/x?$s&counter='), OtpUriError.invalidCounter);
      expect(errorOf('otpauth://totp/x?$s&counter=abc'),
          OtpUriError.invalidCounter);
    });

    test('Unicode spaces in names become plain spaces', () {
      final nbsp = String.fromCharCode(0x00A0);
      final thin = String.fromCharCode(0x2009);
      final a = OtpAccount(
          issuer: 'Git${nbsp}Hub', label: 'a${thin}b', secret: key());
      expect(a.issuer, 'Git Hub');
      expect(a.label, 'a b');
    });

    test('a clock before 2026 is not plausible', () {
      expect(OtpGenerator.clockIsPlausible(DateTime.utc(1969)), isFalse);
      expect(
          OtpGenerator.clockIsPlausible(DateTime.utc(2025, 12, 31)), isFalse);
      expect(OtpGenerator.clockIsPlausible(DateTime.utc(2026, 10, 29)), isTrue);
    });

    test('the model says which rule a value broke', () {
      OtpAccountProblem problemOf(void Function() make) {
        try {
          make();
        } on OtpAccountException catch (e) {
          return e.problem;
        }
        fail('should refuse');
      }

      expect(
          problemOf(
              () => OtpAccount(issuer: '', label: 'a', secret: Uint8List(9))),
          OtpAccountProblem.weakSecret);
      expect(
          problemOf(() => OtpAccount(
              issuer: '',
              label: 'a',
              secret: key(),
              counter: OtpLimits.maxCounter + 1)),
          OtpAccountProblem.counter);
      expect(problemOf(() => OtpAccount(issuer: '', label: ' ', secret: key())),
          OtpAccountProblem.name);
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
