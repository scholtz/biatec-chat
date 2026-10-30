import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_generator.dart';
import 'package:biatec_authenticator/totp/core/otp_uri.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('the account model', () {
    Uint8List key() => Uint8List.fromList(List.generate(20, (i) => 7 + i));

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
      final view = a.secret;
      a.wipe();
      expect(view.every((b) => b == 0), isTrue);
    });

    test(
        'isDuplicateOf compares the secret and parameters, not names, counter or id',
        () {
      final a = OtpAccount(
          issuer: 'I', label: 'L', secret: key(), id: '1', type: OtpType.hotp);
      final b = a.copyWith(id: '2', counter: 9, label: 'other spelling');
      expect(a.isDuplicateOf(b), isTrue);
      final other = Uint8List.fromList(key())..[19] = 8;
      expect(
          a.isDuplicateOf(OtpAccount(
              issuer: 'I', label: 'L', secret: other, type: OtpType.hotp)),
          isFalse);
      expect(
          a.isDuplicateOf(OtpAccount(
              issuer: 'I',
              label: 'L',
              secret: key(),
              digits: 8,
              type: OtpType.hotp)),
          isFalse);
    });

    test('the constructor keeps its own copy of the secret', () {
      final buffer = key();
      final a = OtpAccount(issuer: '', label: 'a', secret: buffer);
      buffer.fillRange(0, buffer.length, 0);
      expect(a.secret, List.generate(20, (i) => 7 + i));
      final view = a.secret;
      a.wipe();
      expect(view.every((b) => b == 0), isTrue);
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
}
