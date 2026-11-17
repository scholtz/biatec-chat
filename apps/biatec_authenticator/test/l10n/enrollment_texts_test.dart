import 'package:biatec_authenticator/enrollment/enrollment_controller.dart';
import 'package:biatec_authenticator/enrollment/enrollment_strings.dart';
import 'package:biatec_authenticator/enrollment/onboarding_texts.dart';
import 'package:biatec_authenticator/enrollment/oidc_enrollment_client.dart';
import 'package:biatec_authenticator/enrollment/widgets/enrollment_problem.dart';
import 'package:biatec_authenticator/keys/key_security_level.dart';
import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';

import 'texts_support.dart';

void main() {
  test('EnrollStrings: the same keys and placeholders in English and Slovak', () {
    expectSameTexts('EnrollStrings', EnrollStrings.en.values, EnrollStrings.sk.values);
  });

  test('OnboardingTexts: the same keys and placeholders in English and Slovak', () {
    expectSameTexts('OnboardingTexts', OnboardingTexts.en.values, OnboardingTexts.sk.values,
        sameInBoth: const {'level_secure_enclave'});
  });

  test('every message key has a text, and the English level names are the ones of KeySecurityLevel', () {
    for (final key in OnboardingTexts.messageKeys) {
      expect(OnboardingTexts.en.values, contains(key));
    }
    for (final level in KeySecurityLevel.values) {
      expect(OnboardingTexts.en.levelLabel(level), level.label);
    }
  });

  group('a message made in English is shown in Slovak', () {
    const sk = OnboardingTexts.sk;

    test('a fixed message', () {
      expect(sk.message(EnrollmentController.legacyKeyNotice), sk.text('msgLegacyKey'));
      expect(sk.message('Malformed enrollment response.'), 'Odpoveď na registráciu má chybný formát.');
    });

    test('a message with a server code keeps the code', () {
      expect(sk.message('Enrollment refused (500): boom'), 'Registrácia bola odmietnutá (500): boom');
    });

    test('a message inside a message is translated too', () {
      expect(sk.message('Enrollment failed: Malformed enrollment response.'),
          'Registrácia zlyhala: Odpoveď na registráciu má chybný formát.');
      expect(sk.message('The provider refused the token answer (400, invalid_grant).'),
          'Poskytovateľ prihlásenia odmietol požiadavku (odpoveď s tokenom; 400, invalid_grant).');
    });

    test('the level of a key is named in Slovak', () {
      expect(sk.message(OnboardingTexts.en.text('msgNotInHardware', {'level': KeySecurityLevel.software.label})),
          'Toto zariadenie neuchováva kľúč v zabezpečenom hardvéri, ale tu: len softvér. Registrácia sa odmieta.');
    });

    test('an unknown text is shown as it came, and English stays English', () {
      expect(sk.message('server said no'), 'server said no');
      expect(OnboardingTexts.en.message('Malformed enrollment response.'), 'Malformed enrollment response.');
    });

    test('the real client messages are known', () {
      try {
        OidcEnrollmentClient.parseResponse(201, '{}', twoKeys: false);
        fail('no refusal');
      } on EnrollmentException catch (e) {
        expect(sk.message(e.message), isNot(e.message));
      }
    });
  });

  test('a failed enrollment in Slovak: the problem and the English reason of the sign-in are Slovak', () {
    const locale = Locale('sk');
    final network = classifyEnrollmentError('Could not reach Biatec OIDC: x', locale: locale);
    expect(network.title, 'Biatec OIDC je nedostupný');
    expect(network.actionLabel, 'Skúsiť znova');
    final key = classifyEnrollmentError(OnboardingTexts.en.text('msgCannotProtect'), locale: locale);
    expect(key.kind, EnrollmentProblemKind.keyNotHardware);
    expect(key.title, 'Toto zariadenie nedokáže ochrániť kľúč');
    expect(key.message, isNot(contains('This device')));
    final login = classifyEnrollmentError(
        '${EnrollmentController.loginFailedPrefix} ${OnboardingTexts.en.text('loginNoBrowser')}',
        locale: locale);
    expect(login.message, 'Prihlásenie zlyhalo: Prehliadač sa nepodarilo otvoriť.');
  });

  test('the countdown says the right Slovak plural', () {
    const sk = OnboardingTexts.sk;
    expect(sk.spokenTimeLeft(const Duration(seconds: 30)), 'Aktivuje sa o menej ako minútu');
    expect(sk.spokenTimeLeft(const Duration(minutes: 1)), 'Aktivuje sa o 1 minútu');
    expect(sk.spokenTimeLeft(const Duration(minutes: 3)), 'Aktivuje sa o 3 minúty');
    expect(sk.spokenTimeLeft(const Duration(hours: 5, minutes: 1)), 'Aktivuje sa o 5 hodín a 1 minútu');
    expect(OnboardingTexts.en.spokenTimeLeft(const Duration(hours: 1, minutes: 2)),
        'Activates in 1 hour and 2 minutes');
  });
}
