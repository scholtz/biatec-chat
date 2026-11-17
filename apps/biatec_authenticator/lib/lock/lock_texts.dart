import 'dart:ui';

import '../l10n/texts.dart';

/// The texts of the app lock and the screens that stand in front of the app (T-251).
class LockTexts extends Texts {
  const LockTexts._(super.values);

  factory LockTexts.of([Locale? locale]) => appLanguageIsSlovak(locale) ? sk : en;

  static const en = LockTexts._({
    'failed': 'That did not match. Try again.',
    'lockout': 'Too many tries. Your phone makes you wait before it checks again. Wait a moment, then try again.',
    'unavailable': 'Your phone cannot check your fingerprint or face right now. Try again in a moment.',
    'locked': 'Locked. Use your fingerprint or face to open it.',
    'lockedDesktop': 'Locked. Confirm that it is you to open it.',
    'integrityTitle': 'This phone failed a safety check',
    'integrityDev': 'This is a development build, so it opens anyway. A release build would stay closed.',
    'notUnlocked': 'Not unlocked',
    'checking': 'Checking',
    'unlock': 'Unlock',
    'noBiometricTitle': 'Set up a fingerprint or face',
    'noBiometricText':
        'This app opens only with your fingerprint or face. It does not accept a PIN, a pattern or a password instead, so a stranger who learns your PIN cannot open it.',
    'toUseIt': 'To use it:',
    'step1': "1. Open your phone's Settings.",
    'step2': '2. Go to Security, then Fingerprint or Face unlock.',
    'step3': '3. Add a fingerprint or your face.',
    'step4': '4. Come back here and tap Check again.',
    'checkAgain': 'Check again',
    'changedTitle': 'Your fingerprints or face changed',
    'changedText':
        'A fingerprint or face was added or removed on this phone. For your safety the keys that protect your codes and your approvals stopped working, and the data they protect cannot be opened.',
    'whatToDo': 'What to do:',
    'changedStep1': '1. Restore your two-factor accounts from your backup file, or add them again at each service.',
    'changedStep2': '2. Enroll this device again to approve actions. Do it from another trusted device if you have one.',
    'restoreFromBackup': 'Restore from a backup file',
    'compromisedTitle': 'This phone cannot be trusted',
    'compromisedText':
        'This phone, or this copy of the app, did not pass a safety check, so the app will not open your codes or approvals here. If you did not change anything, install the app again from the official store, or use another phone.',
    'updateTitle': 'Please update the app',
    'updateExpired':
        'This version of Biatec Authenticator has reached the end of its time. For your safety it will not open your codes or approvals any more.',
    'updateTooOld':
        'This version of Biatec Authenticator is older than the oldest one that is still safe to use, so it will not open your codes or approvals.',
    'updateHow': 'Open {store}, update Biatec Authenticator, then open it again. Your accounts and approvals stay on this phone.',
    'storeApple': 'the App Store',
    'storeGoogle': 'Google Play',
    'delay.immediately': 'Immediately',
    'delay.seconds30': 'After 30 seconds',
    'delay.minute1': 'After 1 minute',
    'delay.minutes5': 'After 5 minutes',
    'reasonDelay': 'Change when the app locks',
    'reasonUnlock': 'Unlock Biatec Authenticator',
    'reasonRestore': 'Restore your accounts from a backup',
  });

  static const sk = LockTexts._({
    'failed': 'Nezhoduje sa. Skúste to znova.',
    'lockout': 'Priveľa pokusov. Telefón vás nechá chvíľu počkať, kým to skontroluje znova. Počkajte chvíľu a skúste to znova.',
    'unavailable': 'Telefón teraz nedokáže overiť váš odtlačok prsta ani tvár. Skúste to o chvíľu znova.',
    'locked': 'Zamknuté. Otvorte aplikáciu odtlačkom prsta alebo tvárou.',
    'lockedDesktop': 'Zamknuté. Na otvorenie potvrďte, že ste to vy.',
    'integrityTitle': 'Tento telefón neprešiel bezpečnostnou kontrolou',
    'integrityDev': 'Toto je vývojárska verzia, preto sa aj tak otvorí. Vydaná verzia by zostala zatvorená.',
    'notUnlocked': 'Neodomknuté',
    'checking': 'Overuje sa',
    'unlock': 'Odomknúť',
    'noBiometricTitle': 'Nastavte si odtlačok prsta alebo tvár',
    'noBiometricText':
        'Táto aplikácia sa otvára iba vaším odtlačkom prsta alebo tvárou. Namiesto nich neprijme PIN, vzor ani heslo, takže ju neotvorí ani cudzí človek, ktorý pozná váš PIN.',
    'toUseIt': 'Ako na to:',
    'step1': '1. Otvorte Nastavenia telefónu.',
    'step2': '2. Prejdite do časti Zabezpečenie a potom Odtlačok prsta alebo Odomknutie tvárou.',
    'step3': '3. Pridajte odtlačok prsta alebo svoju tvár.',
    'step4': '4. Vráťte sa sem a ťuknite na Skontrolovať znova.',
    'checkAgain': 'Skontrolovať znova',
    'changedTitle': 'Vaše odtlačky prstov alebo tvár sa zmenili',
    'changedText':
        'Na tomto telefóne bol pridaný alebo odstránený odtlačok prsta alebo tvár. Pre vašu bezpečnosť prestali fungovať kľúče, ktoré chránia vaše kódy a schválenia, a údaje, ktoré chránia, sa nedajú otvoriť.',
    'whatToDo': 'Čo robiť:',
    'changedStep1': '1. Obnovte svoje dvojfaktorové účty zo záložného súboru alebo ich znova pridajte v každej službe.',
    'changedStep2': '2. Znova zaregistrujte toto zariadenie, aby mohlo schvaľovať. Ak máte iné dôveryhodné zariadenie, urobte to z neho.',
    'restoreFromBackup': 'Obnoviť zo záložného súboru',
    'compromisedTitle': 'Tomuto telefónu sa nedá dôverovať',
    'compromisedText':
        'Tento telefón alebo táto kópia aplikácie neprešla bezpečnostnou kontrolou, preto tu aplikácia neotvorí vaše kódy ani schválenia. Ak ste nič nemenili, nainštalujte aplikáciu znova z oficiálneho obchodu alebo použite iný telefón.',
    'updateTitle': 'Aktualizujte aplikáciu',
    'updateExpired':
        'Platnosť tejto verzie Biatec Authenticator sa skončila. Pre vašu bezpečnosť už neotvorí vaše kódy ani schválenia.',
    'updateTooOld':
        'Táto verzia Biatec Authenticator je staršia ako najstaršia verzia, ktorú je ešte bezpečné používať, preto neotvorí vaše kódy ani schválenia.',
    'updateHow': 'Otvorte {store}, aktualizujte Biatec Authenticator a potom ho znova otvorte. Vaše účty a schválenia zostanú v tomto telefóne.',
    'storeApple': 'App Store',
    'storeGoogle': 'Google Play',
    'delay.immediately': 'Okamžite',
    'delay.seconds30': 'Po 30 sekundách',
    'delay.minute1': 'Po 1 minúte',
    'delay.minutes5': 'Po 5 minútach',
    'reasonDelay': 'Zmeniť, kedy sa aplikácia zamkne',
    'reasonUnlock': 'Odomknúť Biatec Authenticator',
    'reasonRestore': 'Obnoviť účty zo zálohy',
  });
}
