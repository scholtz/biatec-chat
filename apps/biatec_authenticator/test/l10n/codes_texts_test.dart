import 'package:biatec_authenticator/codes/codes_texts.dart';
import 'package:biatec_authenticator/totp/backup/passphrase.dart';
import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:biatec_authenticator/totp/core/otp_account.dart';
import 'package:biatec_authenticator/totp/core/otp_uri.dart';
import 'package:biatec_authenticator/totp/totp_texts.dart';
import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_test/flutter_test.dart';

import 'texts_support.dart';

void main() {
  final binding = TestWidgetsFlutterBinding.ensureInitialized();

  test('the Codes texts: the same keys and placeholders in both languages', () {
    expectSameTexts('CodesTexts', CodesTexts.en.values, CodesTexts.sk.values);
  });

  test(
      'the two-factor engine texts: the same keys and placeholders in both languages',
      () {
    expectSameTexts('TotpTexts', TotpTexts.en.values, TotpTexts.sk.values);
  });

  test('Slovak for sk, English for any other language', () {
    expect(CodesTexts.of(const Locale('sk')), same(CodesTexts.sk));
    expect(CodesTexts.of(const Locale('en')), same(CodesTexts.en));
    expect(CodesTexts.of(const Locale('de')), same(CodesTexts.en));
    expect(TotpTexts.of(const Locale('sk')), same(TotpTexts.sk));
    expect(TotpTexts.of(const Locale('cs')), same(TotpTexts.en));
  });

  test('a count takes the Slovak form for 1, 2 to 4, and 5 or more', () {
    const sk = CodesTexts.sk;
    expect(sk.count('restored', 1), 'Pridal sa 1 účet.');
    expect(sk.count('restored', 3), 'Pridali sa 3 účty.');
    expect(sk.count('restored', 5), 'Pridalo sa 5 účtov.');
    expect(sk.count('restored', 0), 'Pridalo sa 0 účtov.');
    const en = CodesTexts.en;
    expect(en.count('restored', 1), '1 account was added.');
    expect(en.count('restored', 3), '3 accounts were added.');
  });

  group('the engine speaks the language of the device', () {
    tearDown(binding.platformDispatcher.clearLocaleTestValue);

    String uriProblem(String link) {
      try {
        OtpUri.parse(link);
      } on OtpUriException catch (e) {
        return e.message;
      }
      fail('accepted');
    }

    test('Slovak on a Slovak device', () {
      binding.platformDispatcher.localeTestValue = const Locale('sk');
      expect(uriProblem('https://example.com'),
          'Toto nie je odkaz na nastavenie kódu.');
      expect(OtpLimits.digitsMessage, 'Kódy musia mať 6 až 8 číslic.');
      expect(
          () => Base32.decode('   '),
          throwsA(isA<Base32Exception>()
              .having((e) => e.message, 'message', 'Kľúč je prázdny.')));
      expect(Passphrase.check('short').problem,
          startsWith('Použite najmenej 12 znakov.'));
    });

    test('English on any other device', () {
      binding.platformDispatcher.localeTestValue = const Locale('de');
      expect(uriProblem('https://example.com'),
          'This is not a setup link for a code.');
      expect(OtpLimits.digitsMessage, 'Codes must have 6 to 8 digits.');
    });
  });
}
