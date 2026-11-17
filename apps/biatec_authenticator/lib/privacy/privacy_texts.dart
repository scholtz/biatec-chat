import 'dart:ui';

import '../l10n/texts.dart';

/// The texts of the Privacy screen, its inventory ([DataItem] names its texts by key) and the steps of the erase (T-251). English and Slovak.
class PrivacyTexts extends Texts {
  const PrivacyTexts._(super.values);

  /// The texts for [locale] (the phone's language when null).
  factory PrivacyTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  static const en = PrivacyTexts._({
    // The screen.
    'title': 'Privacy',
    'intro':
        'Biatec Authenticator collects only what it needs to work. This is all of it.',
    'stored': 'Stored on this phone',
    'sentBiatec': 'Sent to Biatec OIDC',
    'sentPush': 'Sent to the push service',
    'never': 'Never collected',
    'erase': 'Erase',
    'eraseBody':
        'Removes everything above from this phone. It does not remove what Biatec OIDC holds about your account; cancel this device from another device for that.',
    'refusedTitle': 'Nothing was erased',
    'refusedBody': 'The fingerprint or face check did not pass.',
    'incompleteTitle': 'Some data could not be removed',
    'incompleteBody':
        '{failed}. Try again, or uninstall the app, which removes the rest.',
    'erasing': 'Erasing',
    'eraseButton': 'Erase everything on this phone',
    'confirmTitle': 'Erase everything on this phone?',
    'confirmBody':
        'This removes your two-factor accounts, the device key, your enrollment, your approval history and your settings from this phone, and signs the device out. The app goes back to the start.',
    'confirmWarning':
        'This cannot be undone. Your two-factor accounts are not stored anywhere else: add them again at each service, or restore them from your own backup file. Biatec OIDC keeps its record of this device until you cancel it from another device.',
    'confirmErase': 'Erase everything',
    'keepData': 'Keep my data',
    // Stored on this phone.
    'accounts.title': 'Your two-factor accounts',
    'accounts.text':
        "The service, the account name and the settings of each account are in a file in the app's private folder. The secret key of each one is encrypted with a key that never leaves the phone's security chip and opens only with your fingerprint or face. The keys are not shown again and are not in any backup.",
    'deviceKey.title': 'The device key',
    'deviceKey.text':
        'The key that signs your approvals. It is made inside the security chip and cannot be copied out.',
    'enrollment.title': 'Your enrollment',
    'enrollment.text':
        "The identifier of this device and the token it uses to talk to Biatec OIDC, in the phone's secure storage.",
    'history.title': 'Your approval history',
    'history.text':
        'What you approved or denied, with the amount, who asked and when, so you can read the receipt later. Nothing that would let someone else act on it.',
    'settings.title': 'Your settings',
    'settings.text':
        'The light or dark choice, when the app locks, whether codes are hidden until tapped, and the latest time the app showed codes at.',
    'update.title': 'Update information',
    'update.text':
        'The oldest version of the app that Biatec OIDC still accepts, and a marker that says the storage was upgraded.',
    // Sent to Biatec OIDC.
    'enroll.title': 'When you enroll',
    'enroll.text':
        'The public half of the device key and, where the phone offers it, proof from the phone that this is the real app.',
    'request.title': 'With every request',
    'request.text':
        'The device token and a signature made with the device key, so Biatec OIDC knows the call comes from this phone.',
    'decisions.title': 'Your decisions',
    'decisions.text':
        'The approval or denial you give, signed, and a change to your security settings.',
    'pushAddress.title': 'The notification address',
    'pushAddress.text':
        'Only with the optional Apple wake: the token Apple gave this app, so Biatec OIDC can wake it. The app also keeps one connection open.',
    // Sent to the push service.
    'notice.title': 'A notice that something is waiting',
    'notice.text':
        'Apple delivers it, only with the optional wake. It carries no amount, name or code.',
    // Never collected.
    'neverSecrets':
        'Your two-factor secret keys and codes: they never leave the phone.',
    'neverPictures':
        'Pictures: the camera is used only while you scan a QR code, and nothing is recorded.',
    'neverPersonal': 'Your location, contacts, photos or files.',
    'neverAnalytics':
        'An advertising identifier, analytics or usage statistics. The app has no analytics and no advertising.',
    // The erase: its biometric prompt and the steps that can fail.
    'eraseReason': 'Erase everything on this phone',
    'stepCodes': 'The codes shown on screen',
    'stepAccounts': 'The two-factor accounts',
    'stepVaultKeys': 'The keys that protect the two-factor accounts',
    'stepDeviceKey': 'The device key',
    'stepStorage': 'Settings, history and saved data',
    'stepUpdate': 'Update information',
    'stepEnrollment': 'The enrollment',
  });

  static const sk = PrivacyTexts._({
    'title': 'Súkromie',
    'intro':
        'Biatec Authenticator zbiera iba to, čo potrebuje na svoju prácu. Tu je všetko.',
    'stored': 'Uložené v tomto telefóne',
    'sentBiatec': 'Odosielané do Biatec OIDC',
    'sentPush': 'Odosielané službe push upozornení',
    'never': 'Nikdy sa nezbiera',
    'erase': 'Vymazanie',
    'eraseBody':
        'Odstráni z tohto telefónu všetko uvedené vyššie. Neodstráni to, čo o vašom účte uchováva Biatec OIDC; na to zrušte toto zariadenie z iného zariadenia.',
    'refusedTitle': 'Nič sa nevymazalo',
    'refusedBody': 'Overenie odtlačkom prsta alebo tvárou neprešlo.',
    'incompleteTitle': 'Niektoré údaje sa nepodarilo odstrániť',
    'incompleteBody':
        '{failed}. Skúste to znova alebo aplikáciu odinštalujte, čím sa odstráni zvyšok.',
    'erasing': 'Vymazáva sa',
    'eraseButton': 'Vymazať všetko v tomto telefóne',
    'confirmTitle': 'Vymazať všetko v tomto telefóne?',
    'confirmBody':
        'Z tohto telefónu sa odstránia vaše dvojfaktorové účty, kľúč zariadenia, vaša registrácia, história schválení a nastavenia a zariadenie sa odhlási. Aplikácia sa vráti na začiatok.',
    'confirmWarning':
        'Toto sa nedá vrátiť späť. Vaše dvojfaktorové účty nie sú uložené nikde inde: pridajte ich znova v každej službe alebo ich obnovte zo svojej vlastnej záložnej kópie. Biatec OIDC si záznam o tomto zariadení ponechá, kým ho nezrušíte z iného zariadenia.',
    'confirmErase': 'Vymazať všetko',
    'keepData': 'Ponechať moje údaje',
    'accounts.title': 'Vaše dvojfaktorové účty',
    'accounts.text':
        'Služba, názov účtu a nastavenia každého účtu sú v súbore v súkromnom priečinku aplikácie. Tajný kľúč každého z nich je zašifrovaný kľúčom, ktorý nikdy neopustí bezpečnostný čip telefónu a otvorí sa iba vaším odtlačkom prsta alebo tvárou. Kľúče sa znova nezobrazujú a nie sú v žiadnej záložnej kópii.',
    'deviceKey.title': 'Kľúč zariadenia',
    'deviceKey.text':
        'Kľúč, ktorým sa podpisujú vaše schválenia. Vzniká v bezpečnostnom čipe a nedá sa z neho skopírovať.',
    'enrollment.title': 'Vaša registrácia',
    'enrollment.text':
        'Identifikátor tohto zariadenia a token, ktorým komunikuje s Biatec OIDC, v zabezpečenom úložisku telefónu.',
    'history.title': 'Vaša história schválení',
    'history.text':
        'Čo ste schválili alebo zamietli, so sumou, kto žiadal a kedy, aby ste si potvrdenie mohli neskôr prečítať. Nič, čo by niekomu inému umožnilo konať vo vašom mene.',
    'settings.title': 'Vaše nastavenia',
    'settings.text':
        'Voľba svetlého alebo tmavého vzhľadu, kedy sa aplikácia zamyká, či sú kódy skryté až do ťuknutia, a posledný čas, kedy aplikácia zobrazila kódy.',
    'update.title': 'Informácie o aktualizácii',
    'update.text':
        'Najstaršia verzia aplikácie, ktorú Biatec OIDC ešte prijíma, a značka, že úložisko bolo aktualizované.',
    'enroll.title': 'Pri registrácii',
    'enroll.text':
        'Verejná časť kľúča zariadenia a, ak to telefón ponúka, dôkaz od telefónu, že ide o pravú aplikáciu.',
    'request.title': 'S každou požiadavkou',
    'request.text':
        'Token zariadenia a podpis kľúčom zariadenia, aby Biatec OIDC vedel, že volanie prichádza z tohto telefónu.',
    'decisions.title': 'Vaše rozhodnutia',
    'decisions.text':
        'Podpísané schválenie alebo zamietnutie, ktoré dáte, a zmena vašich nastavení zabezpečenia.',
    'pushAddress.title': 'Adresa pre upozornenia',
    'pushAddress.text':
        'Iba s voliteľným prebúdzaním cez Apple: token, ktorý Apple dal tejto aplikácii, aby ju Biatec OIDC mohol prebudiť. Aplikácia tiež drží otvorené jedno spojenie.',
    'notice.title': 'Oznámenie, že niečo čaká',
    'notice.text':
        'Doručuje ho Apple, iba s voliteľným prebúdzaním. Neobsahuje žiadnu sumu, meno ani kód.',
    'neverSecrets':
        'Vaše dvojfaktorové tajné kľúče a kódy: nikdy neopustia telefón.',
    'neverPictures':
        'Obrázky: fotoaparát sa používa iba počas skenovania QR kódu a nič sa nezaznamenáva.',
    'neverPersonal': 'Vaša poloha, kontakty, fotografie ani súbory.',
    'neverAnalytics':
        'Reklamný identifikátor, analytika ani štatistiky používania. Aplikácia nemá žiadnu analytiku ani reklamu.',
    'eraseReason': 'Vymazať všetko v tomto telefóne',
    'stepCodes': 'Kódy zobrazené na obrazovke',
    'stepAccounts': 'Dvojfaktorové účty',
    'stepVaultKeys': 'Kľúče, ktoré chránia dvojfaktorové účty',
    'stepDeviceKey': 'Kľúč zariadenia',
    'stepStorage': 'Nastavenia, história a uložené údaje',
    'stepUpdate': 'Informácie o aktualizácii',
    'stepEnrollment': 'Registrácia',
  });
}
