import 'dart:ui';

import '../l10n/texts.dart';

/// The texts of the Settings tab (T-251): its groups, the security rows, Appearance, About and the kind of Biatec OIDC server. English and Slovak.
class SettingsTexts extends Texts {
  const SettingsTexts._(super.values);

  /// The texts for [locale] (the phone's language when null).
  factory SettingsTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  static const en = SettingsTexts._({
    'title': 'Settings',
    'security': 'Security',
    'notifications': 'Notifications',
    'appearance': 'Appearance',
    'about': 'About',
    'loadingTitle': 'Loading',
    'loadingBody': 'Getting your security settings.',
    'offlineTitle': 'You are offline',
    'biometricTitle': 'Biometric check',
    'biometricBody':
        'Always required. Your fingerprint or face opens the app and confirms every approval. It cannot be turned off.',
    'thresholdTitle': 'Approval threshold',
    'thresholdSubtitle':
        '{amount} EUR. Actions above it need your phone. Raising it needs approval on this device.',
    'change': 'Change',
    'blockUnverified': 'Block applications without a proven domain',
    'blockUnverifiedHelp':
        'Applications that have not proven their domain cannot ask for approvals. Turning it off needs approval on this device.',
    'blockUnverifiedOffTitle': 'Let applications without a proven domain ask?',
    'blockUnverifiedOffBody':
        'Applications that have not proven their domain will be able to ask for approvals.',
    'turnOff': 'Turn off',
    'trustedClients': 'Trusted clients and thresholds',
    'trustedClientsHelp': 'Per-client limits, "always ask" and pauses.',
    'version': 'Version',
    'oidcServer': 'Biatec OIDC server',
    'privacy': 'Privacy',
    'privacyHelp':
        'What the app stores and sends, and erasing everything from this phone.',
    'licenses': 'Open-source notices',
    'licensesHelp': 'The libraries this app is built with.',
    'themeSystem': 'System',
    'themeSystemHelp': "Follows your phone's setting.",
    'themeLight': 'Light',
    'themeLightHelp': 'Always light.',
    'themeDark': 'Dark',
    'themeDarkHelp': 'Always dark.',
    'chosen': 'Chosen',
    'keyTitle': 'Device key protection',
    'keyChecking': 'Checking.',
    'keyNoAnswer':
        'The keystore did not answer. Close and open the app to try again.',
    'keyNone': 'No key found for this device.',
    'keyHardware': 'Kept in {level}. It cannot be exported.',
    'keySoftware': 'Not in secure hardware ({level}).',
    'lockTitle': 'Lock the app',
    'lockSubtitle':
        '{delay} after you leave it. It also locks when the screen locks and after 2 minutes without a touch.',
    'lockFailed': 'Biometric check did not pass. Nothing changed.',
    'endpointMock': 'Mock server for testing. Nothing here is real.',
    'endpointStage': 'Stage server. For testing before release.',
    'endpointLocal': 'Test server on a local network.',
    'endpointLive': 'Live server.',
  });

  static const sk = SettingsTexts._({
    'title': 'Nastavenia',
    'security': 'Zabezpečenie',
    'notifications': 'Upozornenia',
    'appearance': 'Vzhľad',
    'about': 'O aplikácii',
    'loadingTitle': 'Načítava sa',
    'loadingBody': 'Získavajú sa vaše nastavenia zabezpečenia.',
    'offlineTitle': 'Ste offline',
    'biometricTitle': 'Biometrické overenie',
    'biometricBody':
        'Vždy povinné. Odtlačok prsta alebo tvár otvára aplikáciu a potvrdzuje každé schválenie. Nedá sa vypnúť.',
    'thresholdTitle': 'Limit na schvaľovanie',
    'thresholdSubtitle':
        '{amount} EUR. Akcie nad ním vyžadujú váš telefón. Jeho zvýšenie treba schváliť v tomto zariadení.',
    'change': 'Zmeniť',
    'blockUnverified': 'Blokovať aplikácie bez overenej domény',
    'blockUnverifiedHelp':
        'Aplikácie, ktoré si neoverili doménu, nemôžu žiadať o schválenie. Vypnutie treba schváliť v tomto zariadení.',
    'blockUnverifiedOffTitle': 'Povoliť aplikácie bez overenej domény?',
    'blockUnverifiedOffBody':
        'Aplikácie, ktoré si neoverili doménu, budú môcť žiadať o schválenie.',
    'turnOff': 'Vypnúť',
    'trustedClients': 'Dôveryhodní klienti a limity',
    'trustedClientsHelp':
        'Limity pre jednotlivých klientov, „vždy sa pýtať“ a pozastavenia.',
    'version': 'Verzia',
    'oidcServer': 'Server Biatec OIDC',
    'privacy': 'Súkromie',
    'privacyHelp':
        'Čo aplikácia ukladá a odosiela a vymazanie všetkého z tohto telefónu.',
    'licenses': 'Licencie open source',
    'licensesHelp': 'Knižnice, z ktorých je aplikácia zostavená.',
    'themeSystem': 'Systém',
    'themeSystemHelp': 'Riadi sa nastavením telefónu.',
    'themeLight': 'Svetlý',
    'themeLightHelp': 'Vždy svetlý.',
    'themeDark': 'Tmavý',
    'themeDarkHelp': 'Vždy tmavý.',
    'chosen': 'Vybrané',
    'keyTitle': 'Ochrana kľúča zariadenia',
    'keyChecking': 'Overuje sa.',
    'keyNoAnswer':
        'Úložisko kľúčov neodpovedalo. Zatvorte a znova otvorte aplikáciu a skúste to znova.',
    'keyNone': 'Pre toto zariadenie sa nenašiel žiadny kľúč.',
    'keyHardware': 'Uchováva ho: {level}. Nedá sa exportovať.',
    'keySoftware': 'Nie je v zabezpečenom hardvéri ({level}).',
    'lockTitle': 'Zamknúť aplikáciu',
    'lockSubtitle':
        '{delay} po odchode z aplikácie. Zamkne sa aj pri zamknutí obrazovky a po 2 minútach bez dotyku.',
    'lockFailed': 'Biometrické overenie neprešlo. Nič sa nezmenilo.',
    'endpointMock': 'Testovací server (mock). Nič tu nie je skutočné.',
    'endpointStage': 'Server stage. Na testovanie pred vydaním.',
    'endpointLocal': 'Testovací server v lokálnej sieti.',
    'endpointLive': 'Produkčný server.',
  });
}
