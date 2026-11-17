import 'dart:ui';

import '../l10n/texts.dart';

/// The messages of the device keys that a screen shows (T-251): approvals switched off, keys gone, a key in software, a signature that failed.
class KeyTexts extends Texts {
  const KeyTexts._(super.values);

  factory KeyTexts.of([Locale? locale]) => appLanguageIsSlovak(locale) ? sk : en;

  static const en = KeyTexts._({
    'invalidated':
        'The fingerprint or face settings on this phone changed, so approvals are switched off on this device. You can still deny requests. Enroll this device again to approve.',
    'missing': 'The device keys are gone from this phone. Enroll this device again.',
    'softwareKey':
        "This device's key is held in software, not in secure hardware, so it cannot approve. Remove this device from your account on another trusted device and enroll it again.",
    'cancelled': 'The biometric check was cancelled.',
    'couldNotSign': 'The device key could not sign. Try again.',
    'checkFailed': 'The biometric check did not succeed.',
  });

  static const sk = KeyTexts._({
    'invalidated':
        'Nastavenia odtlačkov prstov alebo tváre na tomto telefóne sa zmenili, preto je schvaľovanie na tomto zariadení vypnuté. Žiadosti môžete stále zamietať. Ak chcete schvaľovať, zaregistrujte toto zariadenie znova.',
    'missing': 'Kľúče zariadenia z tohto telefónu zmizli. Zaregistrujte toto zariadenie znova.',
    'softwareKey':
        'Kľúč tohto zariadenia je uložený v softvéri, nie v zabezpečenom hardvéri, preto nemôže schvaľovať. Odstráňte toto zariadenie zo svojho účtu na inom dôveryhodnom zariadení a zaregistrujte ho znova.',
    'cancelled': 'Biometrické overenie bolo zrušené.',
    'couldNotSign': 'Kľúč zariadenia nedokázal podpísať. Skúste to znova.',
    'checkFailed': 'Biometrické overenie sa nepodarilo.',
  });
}
