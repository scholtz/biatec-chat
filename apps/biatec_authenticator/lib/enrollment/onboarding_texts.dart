import 'dart:ui';

import '../keys/key_security_level.dart';
import '../l10n/texts.dart';

/// The texts of the onboarding screens (T-251): the steps, the screens, the recovery code, the key protection card, the problems of a failed enrollment, and
/// the messages the enrollment controller and the login clients make.
///
/// The controller and the clients make their messages in English (`OnboardingTexts.en`), because the screens recognise them by that text
/// ([classifyEnrollmentError]); the screen shows them in its own language with [message], which reads an English message back from its template.
class OnboardingTexts extends Texts {
  const OnboardingTexts._(super.values);

  /// The texts for [locale] (the device's language when null).
  factory OnboardingTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  /// The keys of the messages the controller and the clients make: [message] knows them.
  static const messageKeys = [
    'msgLegacyKey',
    'msgCannotProtect',
    'msgLevelUnknown',
    'msgNotInHardware',
    'msgFreshLogin',
    'msgNoPin',
    'msgPromptActive',
    'msgNoBiometrics',
    'msgKeysFailed',
    'msgKeysError',
    'msgEnrollCancelled',
    'msgNoChallenge',
    'msgProofFailed',
    'msgEnrollFailed',
    'msgNotSaved',
    'msgNotSavedWhy',
    'msgConfirmingAgain',
    'msgSaveLocalFailed',
    'msgUnreachable',
    'msgRefused',
    'msgBothKeys',
    'msgMalformed',
    'msgUnexpected',
    'msgRecoveryUnavailable',
    'msgRecoveryAckFailed',
    'msgPinning',
    'loginAlreadyOpen',
    'loginNoLoopback',
    'loginNoBrowser',
    'loginUnreachable',
    'loginTimeout',
    'loginTls',
    'loginIncomplete',
    'loginNoEndpoints',
    'loginEndpointsHttps',
    'loginDiscoveryHttps',
    'loginOtherIssuer',
    'loginNoPkce',
    'loginBadEndpoint',
    'loginPortInUse',
    'loginPortFailed',
    'loginProviderRefused',
    'loginNotInTime',
    'loginNoAccessToken',
    'loginBadTokenType',
    'loginNoIdToken',
    'loginIdUnreadable',
    'loginIdNotMine',
    'loginIdOtherIssuer',
    'loginEarlierSignIn',
    'loginIdOtherApp',
    'loginNoAnswerWhat',
    'loginTooLarge',
    'loginRefusedWhat',
    'loginNotJson',
    'loginWhatDiscovery',
    'loginWhatToken',
    'loginIos',
    'loginNoAccessTokenOidc',
  ];

  /// The placeholders whose value is itself a message of this app (and is shown in the same language); any other value (a server's code, a host, a number) is
  /// shown as it came.
  static const _nested = {'why', 'what'};

  static final _placeholder = RegExp(r'\{([a-zA-Z0-9_]+)\}');

  static final Map<String, (RegExp, List<String>)> _patterns = {
    for (final key in messageKeys) key: _pattern(en.values[key]!),
  };

  static (RegExp, List<String>) _pattern(String template) {
    final names = <String>[];
    final source = StringBuffer('^');
    var at = 0;
    for (final m in _placeholder.allMatches(template)) {
      source.write(RegExp.escape(template.substring(at, m.start)));
      source.write(r'([\s\S]*)');
      names.add(m.group(1)!);
      at = m.end;
    }
    source.write(RegExp.escape(template.substring(at)));
    source.write(r'$');
    return (RegExp(source.toString()), names);
  }

  /// [english], a message the controller or a client made (from `OnboardingTexts.en`), in this language. A text that is none of them (a server's own words, or
  /// a message made elsewhere) is returned as it is.
  String message(String english) {
    if (identical(this, en)) return english;
    for (final key in messageKeys) {
      final (pattern, names) = _patterns[key]!;
      final m = pattern.firstMatch(english);
      if (m == null) continue;
      return text(key, {
        for (var i = 0; i < names.length; i++)
          names[i]: _arg(names[i], m.group(i + 1)!),
      });
    }
    return english;
  }

  String _arg(String name, String value) {
    if (_nested.contains(name)) return message(value);
    if (name == 'level') {
      for (final level in KeySecurityLevel.values) {
        if (level.label == value) return levelLabel(level);
      }
    }
    return value;
  }

  /// Where the key is kept ([KeySecurityLevel.label]) in this language.
  String levelLabel(KeySecurityLevel level) => text('level_${level.wire}');

  /// "Activates in 2 hours and 5 minutes": whole hours and minutes, with the right plural form.
  String spokenTimeLeft(Duration d) {
    if (d.inMinutes < 1) return text('activatesSoon');
    final m = d.inMinutes.remainder(60);
    return d.inHours == 0
        ? text('activatesIn', {'time': _count(m, 'minute')})
        : text('activatesInHours', {
            'hours': _count(d.inHours, 'hour'),
            'minutes': _count(m, 'minute'),
          });
  }

  /// [n] with its unit: one (1), few (2 to 4, a Slovak form) or many.
  String _count(int n, String unit) {
    final form = n == 1
        ? 'One'
        : n >= 2 && n <= 4
            ? 'Few'
            : 'Many';
    return text('$unit$form', {'n': n});
  }

  static const en = OnboardingTexts._({
    // The steps
    'appBarTitle': 'Set up this device',
    'stepLine': 'Step {n} of {count} · {label}',
    'step_welcome': 'Welcome',
    'step_connect': 'Connect',
    'step_protect': 'Protect the key',
    'step_recovery': 'Recovery code',
    'step_done': 'Done',

    // The countdown
    'activatesSoon': 'Activates in less than a minute',
    'activatesIn': 'Activates in {time}',
    'activatesInHours': 'Activates in {hours} and {minutes}',
    'minuteOne': '{n} minute',
    'minuteFew': '{n} minutes',
    'minuteMany': '{n} minutes',
    'hourOne': '{n} hour',
    'hourFew': '{n} hours',
    'hourMany': '{n} hours',
    'timeLeft': 'Time left: {time}',

    // Welcome
    'storageUnreadable':
        'The storage of this device could not be read. Try again.',
    'noEnrollmentYet': 'This app has no enrollment for this device yet.',
    'welcomeTitle': 'Welcome to Biatec Authenticator',
    'welcomeIntro':
        'This app is the second factor for your Biatec account: it asks you to approve what matters, on this device, with your fingerprint or face.',
    'legacyKeyTitle': 'Set this device up again',
    'holdsTitle': 'What it holds',
    'holdsText':
        'Two keys created in this device\'s secure hardware that never leave it (the one that approves needs your fingerprint or face every time), and a device token that proves this device to Biatec OIDC.',
    'neverHoldsTitle': 'What it never holds',
    'neverHoldsText':
        'Your wallet keys, your funds or your passwords. It cannot move money by itself.',
    'lostTitle': 'If you lose this device',
    'lostText':
        'A recovery code, shown once at the end of setup, activates a replacement device.',
    'getStarted': 'Get started',

    // Connect
    'connectTitle': 'Connect this device to your account',
    'pasteIntro':
        'Paste the login token from a fresh Biatec OIDC login. It is used once, to link this device, and is not kept.',
    'checking': 'Checking…',
    'loginTokenLabel': 'Login token',
    'loginTokenHelpMock': 'Debug builds only: a seeded token of the mock',
    'loginTokenHelp': 'From a fresh Biatec OIDC login',
    'hideToken': 'Hide the token',
    'showToken': 'Show the token',
    'connectPasted': 'Connect with the pasted token',
    'connectCreate': 'Connect and create the key',
    'connectAgain': 'Connect again with this token',

    // Protect the key
    'inactiveTitle': 'Device inactive',
    'inactiveText':
        'A new device can approve requests only after a waiting period. It protects your account if someone else gets hold of a login. Until then, actions that need phone approval cannot be completed here.',
    'protectTitle': 'Protecting the key',
    'protectText':
        'The key is created in this device\'s secure hardware. It cannot be exported, and it is used only after your fingerprint or face.',
    'working': 'Working',
    'creatingKey': 'Creating the device key',
    'enrollingDevice': 'Enrolling this device',
    'cancelFailed': 'Could not cancel this device.',

    // Recovery code and done
    'recoveryTitle': 'Your recovery code',
    'recoveryLoadFailed': 'The recovery code could not be loaded',
    'tryAgain': 'Try again',
    'doneTitle': 'This device is enrolled and active.',
    'doneText':
        'You can now approve requests from Biatec apps on this device.',
    'rcTitle': 'Recovery code',
    'rcIntro':
        'Write this code down and keep it safe. It activates a replacement device immediately.',
    'rcOnceTitle': 'Shown only once',
    'rcOnce': 'You cannot see this code again after you continue.',
    'rcSemantics': 'Recovery code: {chars}',
    'rcDash': 'dash',
    'rcConfirmFailedTitle': 'Could not confirm',
    'rcConfirmFailed': '{why} Your code is still shown. Try again.',
    'rcWritten': 'I have written the code down and keep it somewhere safe.',
    'rcSaved': 'I have saved it',

    // The key protection card
    'hwTitle': 'Hardware-protected key',
    'hwMessage': 'Key protection: {level}. The key cannot be exported.',
    'swTitle': 'Software-only key',
    'swMessage':
        'Key protection: {level}. This is not secure hardware, so it is not accepted.',
    'unknownTitle': 'Key protection unknown',
    'unknownMessage':
        'Key protection: unknown. This device did not say where the key is kept.',
    'level_secure_enclave': 'Secure Enclave',
    'level_strongbox': 'StrongBox security chip',
    'level_tee': 'trusted execution environment',
    'level_hardware': 'secure hardware',
    'level_tpm': 'TPM security chip',
    'level_windows_account': 'your Windows account (software)',
    'level_software': 'software only',
    'level_unknown': 'unknown',

    // The problems of a failed enrollment
    'unverifiedTitle': 'The connection could not be verified',
    'unverified': 'Nothing was sent. If this keeps happening, update the app.',
    'networkTitle': 'Cannot reach Biatec OIDC',
    'network': 'Check your connection and try again. Nothing was enrolled.',
    'tokenNeededTitle': 'Login token needed',
    'tokenNeeded':
        'Sign in to Biatec OIDC to get a fresh login token, paste it below and connect.',
    'alreadyTitle': 'This device is already enrolled',
    'already':
        'Biatec OIDC already has this device. If this app has the enrollment, check again to open it. If it does not, remove the device from your account on another trusted device, then connect again.',
    'checkAgain': 'Check again',
    'tokenRefusedTitle': 'Login token not accepted',
    'tokenRefused':
        'The token was refused, perhaps because it is old or was already used. Sign in to Biatec OIDC again to get a fresh one, replace the token below and connect.',
    'keyNotHardwareTitle': 'This device cannot protect the key',
    'keyNotHardware':
        'The keys must be kept in secure hardware, and the one that approves must need your fingerprint or face every time. {why}',
    'enrollFailedTitle': 'Enrollment failed',

    // The prompt of the enrollment (the platform shows it)
    'promptTitleComputer': 'Enroll this computer',
    'promptTitlePhone': 'Enroll this phone',
    'promptSubtitleComputer': 'Approvals will need this check',
    'promptSubtitlePhone': 'Approvals will need your fingerprint or face',

    // The messages of the controller
    'msgLegacyKey':
        'This phone has an older device key that is not protected by your fingerprint or face. Enroll it again to approve.',
    'msgCannotProtect':
        'This device cannot protect approvals. Its keystore cannot make a key that needs your confirmation (fingerprint, face, Windows Hello or a PIN of the app) for every approval, so enrollment is refused.',
    'msgLevelUnknown':
        'This device cannot say where it keeps the key, so it is not treated as secure hardware. Enrollment is refused.',
    'msgNotInHardware':
        'This device keeps the key in {level}, not in secure hardware. Enrollment is refused.',
    'msgFreshLogin': 'A fresh login is required to enroll.',
    'msgNoPin':
        'No PIN was chosen, so nothing was enrolled. Sign in again and choose a PIN.',
    'msgPromptActive':
        'Another prompt of this app is open. Close it, then sign in again.',
    'msgNoBiometrics':
        'Set up a fingerprint or face on this phone first: approvals need one.',
    'msgKeysFailed':
        'This device could not create the device keys. Restart it and try again.',
    'msgKeysError': 'Could not create the device keys: {why}',
    'msgEnrollCancelled': 'Enrollment cancelled. Nothing was enrolled.',
    'msgNoChallenge':
        'Biatec OIDC did not give an enrollment challenge. Try again.',
    'msgProofFailed': 'Could not prove possession of the device keys: {why}',
    'msgEnrollFailed': 'Enrollment failed: {why}',
    'msgNotSaved': 'Enrolled, but this device could not save it yet.',
    'msgNotSavedWhy': 'Enrolled, but this device could not save it yet: {why}',
    'msgConfirmingAgain': 'Confirming again...',
    'msgSaveLocalFailed': 'Could not save locally: {why}',

    // The messages of the enrollment client
    'msgUnreachable': 'Could not reach Biatec OIDC: {why}',
    'msgRefused': 'Enrollment refused ({status}): {detail}',
    'msgBothKeys': 'Biatec OIDC did not enroll both device keys.',
    'msgMalformed': 'Malformed enrollment response.',
    'msgUnexpected': 'Unexpected response ({status}).',
    'msgRecoveryUnavailable': 'Recovery code unavailable ({status}): {detail}',
    'msgRecoveryAckFailed': 'Could not confirm the recovery code ({status}).',
    // PinningException.message (lib/security/pinning/pinned_http.dart), shown here in the language of the screen.
    'msgPinning':
        'The connection to {host} could not be verified, so nothing was sent. If this keeps happening, update the app.',

    // The messages of the login clients
    'loginAlreadyOpen': 'A sign-in is already open.',
    'loginNoLoopback': 'This build has no loopback redirect to sign in with.',
    'loginNoBrowser': 'The browser could not be opened.',
    'loginUnreachable': 'The provider could not be reached ({detail}).',
    'loginTimeout': 'The provider did not answer in time.',
    'loginTls':
        'The connection to the provider could not be verified ({detail}).',
    'loginIncomplete': 'The sign-in could not be completed ({detail}).',
    'loginNoEndpoints': 'This build names no login endpoints.',
    'loginEndpointsHttps': 'The login endpoints must be https.',
    'loginDiscoveryHttps': 'The discovery address must be https.',
    'loginOtherIssuer': 'The discovery document names another issuer.',
    'loginNoPkce': 'The provider does not support PKCE with S256.',
    'loginBadEndpoint':
        'The {name} of the provider is not an https address on {host}.',
    'loginPortInUse':
        'Port {port} on this computer is in use. Close the program that uses it and sign in again.',
    'loginPortFailed':
        'Port {port} on this computer cannot be opened for the sign-in ({detail}).',
    'loginProviderRefused': 'The provider refused the sign-in ({code}).',
    'loginNotInTime': 'The sign-in was not finished in time.',
    'loginNoAccessToken': 'The provider gave no access token.',
    'loginBadTokenType':
        'The provider gave a token of a kind this app cannot use.',
    'loginNoIdToken': 'The provider gave no ID token.',
    'loginIdUnreadable': 'The ID token of the provider cannot be read.',
    'loginIdNotMine': 'The ID token is not for this sign-in.',
    'loginIdOtherIssuer': 'The ID token is from another issuer.',
    'loginEarlierSignIn':
        'The provider used an earlier sign-in. Sign out in the browser and sign in again.',
    'loginIdOtherApp': 'The ID token is for another application.',
    'loginNoAnswerWhat': 'The provider did not answer in time ({what}).',
    'loginTooLarge': 'The {what} of the provider is too large.',
    'loginRefusedWhat': 'The provider refused the {what} ({status}, {code}).',
    'loginNotJson': 'The {what} of the provider is not JSON.',
    'loginWhatDiscovery': 'discovery document',
    'loginWhatToken': 'token answer',
    'loginIos':
        'Signing in from this app needs iOS 17.4 or later. Update this phone, or enroll from a newer one.',
    'loginNoAccessTokenOidc': 'Biatec OIDC gave no access token.',

    // The pages the desktop sign-in shows in the browser
    'pageDone':
        'The sign-in was received. Go back to Biatec Authenticator to continue. You can close this window.',
    'pageFailed':
        'The sign-in was not completed. Close this window and go back to Biatec Authenticator.',
    'pageStray':
        'This address is used by Biatec Authenticator while it signs in. Nothing was done.',
  });

  static const sk = OnboardingTexts._({
    // The steps
    'appBarTitle': 'Nastavenie tohto zariadenia',
    'stepLine': 'Krok {n} z {count} · {label}',
    'step_welcome': 'Vitajte',
    'step_connect': 'Pripojenie',
    'step_protect': 'Ochrana kľúča',
    'step_recovery': 'Obnovovací kód',
    'step_done': 'Hotovo',

    // The countdown
    'activatesSoon': 'Aktivuje sa o menej ako minútu',
    'activatesIn': 'Aktivuje sa o {time}',
    'activatesInHours': 'Aktivuje sa o {hours} a {minutes}',
    'minuteOne': '{n} minútu',
    'minuteFew': '{n} minúty',
    'minuteMany': '{n} minút',
    'hourOne': '{n} hodinu',
    'hourFew': '{n} hodiny',
    'hourMany': '{n} hodín',
    'timeLeft': 'Zostáva: {time}',

    // Welcome
    'storageUnreadable':
        'Úložisko tohto zariadenia sa nepodarilo prečítať. Skúste znova.',
    'noEnrollmentYet':
        'Táto aplikácia zatiaľ nemá pre toto zariadenie žiadnu registráciu.',
    'welcomeTitle': 'Vitajte v aplikácii Biatec Authenticator',
    'welcomeIntro':
        'Táto aplikácia je druhým faktorom vášho účtu Biatec: to, na čom záleží, vás požiada schváliť na tomto zariadení odtlačkom prsta alebo tvárou.',
    'legacyKeyTitle': 'Nastavte toto zariadenie znova',
    'holdsTitle': 'Čo uchováva',
    'holdsText':
        'Dva kľúče vytvorené v zabezpečenom hardvéri tohto zariadenia, ktoré ho nikdy neopustia (ten, ktorým sa schvaľuje, zakaždým vyžaduje váš odtlačok prsta alebo tvár), a token zariadenia, ktorým sa toto zariadenie preukazuje voči Biatec OIDC.',
    'neverHoldsTitle': 'Čo nikdy neuchováva',
    'neverHoldsText':
        'Kľúče vašej peňaženky, vaše prostriedky ani vaše heslá. Sama nedokáže presunúť peniaze.',
    'lostTitle': 'Ak toto zariadenie stratíte',
    'lostText':
        'Obnovovací kód, ktorý sa zobrazí raz na konci nastavenia, aktivuje náhradné zariadenie.',
    'getStarted': 'Začať',

    // Connect
    'connectTitle': 'Pripojte toto zariadenie k svojmu účtu',
    'pasteIntro':
        'Vložte prihlasovací token z čerstvého prihlásenia do Biatec OIDC. Použije sa raz, na prepojenie tohto zariadenia, a neuchováva sa.',
    'checking': 'Kontrolujem…',
    'loginTokenLabel': 'Prihlasovací token',
    'loginTokenHelpMock':
        'Len ladiace zostavy: pripravený token simulovaného servera',
    'loginTokenHelp': 'Z čerstvého prihlásenia do Biatec OIDC',
    'hideToken': 'Skryť token',
    'showToken': 'Zobraziť token',
    'connectPasted': 'Pripojiť s vloženým tokenom',
    'connectCreate': 'Pripojiť a vytvoriť kľúč',
    'connectAgain': 'Pripojiť znova s týmto tokenom',

    // Protect the key
    'inactiveTitle': 'Zariadenie je neaktívne',
    'inactiveText':
        'Nové zariadenie môže schvaľovať požiadavky až po čakacej lehote. Chráni to váš účet, ak by sa k prihláseniu dostal niekto iný. Dovtedy sa tu nedajú dokončiť úkony, ktoré vyžadujú schválenie na telefóne.',
    'protectTitle': 'Zabezpečenie kľúča',
    'protectText':
        'Kľúč sa vytvorí v zabezpečenom hardvéri tohto zariadenia. Nedá sa exportovať a použije sa až po overení odtlačkom prsta alebo tvárou.',
    'working': 'Pracujem',
    'creatingKey': 'Vytváram kľúč zariadenia',
    'enrollingDevice': 'Registrujem toto zariadenie',
    'cancelFailed': 'Toto zariadenie sa nepodarilo zrušiť.',

    // Recovery code and done
    'recoveryTitle': 'Váš obnovovací kód',
    'recoveryLoadFailed': 'Obnovovací kód sa nepodarilo načítať',
    'tryAgain': 'Skúsiť znova',
    'doneTitle': 'Toto zariadenie je zaregistrované a aktívne.',
    'doneText':
        'Teraz môžete na tomto zariadení schvaľovať požiadavky z aplikácií Biatec.',
    'rcTitle': 'Obnovovací kód',
    'rcIntro':
        'Tento kód si zapíšte a dobre uschovajte. Okamžite aktivuje náhradné zariadenie.',
    'rcOnceTitle': 'Zobrazí sa len raz',
    'rcOnce': 'Keď budete pokračovať, tento kód už znova neuvidíte.',
    'rcSemantics': 'Obnovovací kód: {chars}',
    'rcDash': 'pomlčka',
    'rcConfirmFailedTitle': 'Nepodarilo sa potvrdiť',
    'rcConfirmFailed': '{why} Váš kód sa stále zobrazuje. Skúste znova.',
    'rcWritten': 'Kód mám zapísaný a uschovaný na bezpečnom mieste.',
    'rcSaved': 'Kód mám uschovaný',

    // The key protection card
    'hwTitle': 'Kľúč chránený hardvérom',
    'hwMessage': 'Ochrana kľúča: {level}. Kľúč sa nedá exportovať.',
    'swTitle': 'Kľúč len v softvéri',
    'swMessage':
        'Ochrana kľúča: {level}. Nie je to zabezpečený hardvér, preto sa neprijíma.',
    'unknownTitle': 'Ochrana kľúča je neznáma',
    'unknownMessage':
        'Ochrana kľúča: neznáma. Toto zariadenie neuviedlo, kde je kľúč uložený.',
    'level_secure_enclave': 'Secure Enclave',
    'level_strongbox': 'bezpečnostný čip StrongBox',
    'level_tee': 'dôveryhodné prostredie na vykonávanie kódu (TEE)',
    'level_hardware': 'zabezpečený hardvér',
    'level_tpm': 'bezpečnostný čip TPM',
    'level_windows_account': 'váš účet vo Windows (softvér)',
    'level_software': 'len softvér',
    'level_unknown': 'neznáma',

    // The problems of a failed enrollment
    'unverifiedTitle': 'Spojenie sa nepodarilo overiť',
    'unverified': 'Nič sa neodoslalo. Ak sa to opakuje, aktualizujte aplikáciu.',
    'networkTitle': 'Biatec OIDC je nedostupný',
    'network': 'Skontrolujte pripojenie a skúste znova. Nič sa nezaregistrovalo.',
    'tokenNeededTitle': 'Je potrebný prihlasovací token',
    'tokenNeeded':
        'Prihláste sa do Biatec OIDC, získajte čerstvý prihlasovací token, vložte ho nižšie a pripojte sa.',
    'alreadyTitle': 'Toto zariadenie je už zaregistrované',
    'already':
        'Biatec OIDC už toto zariadenie má. Ak má registráciu aj táto aplikácia, skontrolujte to znova a otvorí sa. Ak ju nemá, odstráňte zariadenie zo svojho účtu na inom dôveryhodnom zariadení a potom sa pripojte znova.',
    'checkAgain': 'Skontrolovať znova',
    'tokenRefusedTitle': 'Prihlasovací token nebol prijatý',
    'tokenRefused':
        'Token bol odmietnutý, možno preto, že je starý alebo už bol použitý. Znova sa prihláste do Biatec OIDC, získajte nový, nahraďte ním token nižšie a pripojte sa.',
    'keyNotHardwareTitle': 'Toto zariadenie nedokáže ochrániť kľúč',
    'keyNotHardware':
        'Kľúče musia byť uložené v zabezpečenom hardvéri a ten, ktorým sa schvaľuje, musí zakaždým vyžadovať váš odtlačok prsta alebo tvár. {why}',
    'enrollFailedTitle': 'Registrácia zlyhala',

    // The prompt of the enrollment (the platform shows it)
    'promptTitleComputer': 'Zaregistrovať tento počítač',
    'promptTitlePhone': 'Zaregistrovať tento telefón',
    'promptSubtitleComputer': 'Schválenia budú vyžadovať toto overenie',
    'promptSubtitlePhone':
        'Schválenia budú vyžadovať váš odtlačok prsta alebo tvár',

    // The messages of the controller
    'msgLegacyKey':
        'Tento telefón má starší kľúč zariadenia, ktorý nie je chránený vaším odtlačkom prsta ani tvárou. Ak chcete schvaľovať, zaregistrujte ho znova.',
    'msgCannotProtect':
        'Toto zariadenie nedokáže ochrániť schválenia. Jeho úložisko kľúčov nevie vytvoriť kľúč, ktorý by pri každom schválení vyžadoval vaše potvrdenie (odtlačok prsta, tvár, Windows Hello alebo PIN aplikácie), preto sa registrácia odmieta.',
    'msgLevelUnknown':
        'Toto zariadenie nevie povedať, kde uchováva kľúč, preto sa nepovažuje za zabezpečený hardvér. Registrácia sa odmieta.',
    'msgNotInHardware':
        'Toto zariadenie neuchováva kľúč v zabezpečenom hardvéri, ale tu: {level}. Registrácia sa odmieta.',
    'msgFreshLogin': 'Na registráciu je potrebné čerstvé prihlásenie.',
    'msgNoPin':
        'Nezvolili ste PIN, preto sa nič nezaregistrovalo. Prihláste sa znova a zvoľte PIN.',
    'msgPromptActive':
        'Je otvorená iná výzva tejto aplikácie. Zatvorte ju a potom sa prihláste znova.',
    'msgNoBiometrics':
        'Najprv si na tomto telefóne nastavte odtlačok prsta alebo tvár: schválenia ich vyžadujú.',
    'msgKeysFailed':
        'Toto zariadenie nedokázalo vytvoriť kľúče zariadenia. Reštartujte ho a skúste znova.',
    'msgKeysError': 'Kľúče zariadenia sa nepodarilo vytvoriť: {why}',
    'msgEnrollCancelled': 'Registrácia bola zrušená. Nič sa nezaregistrovalo.',
    'msgNoChallenge': 'Biatec OIDC neposkytol výzvu na registráciu. Skúste znova.',
    'msgProofFailed': 'Nepodarilo sa preukázať vlastníctvo kľúčov zariadenia: {why}',
    'msgEnrollFailed': 'Registrácia zlyhala: {why}',
    'msgNotSaved':
        'Zariadenie je zaregistrované, ale zatiaľ sa ho nepodarilo uložiť.',
    'msgNotSavedWhy':
        'Zariadenie je zaregistrované, ale zatiaľ sa ho nepodarilo uložiť: {why}',
    'msgConfirmingAgain': 'Potvrdzujem znova...',
    'msgSaveLocalFailed': 'Nepodarilo sa uložiť v tomto zariadení: {why}',

    // The messages of the enrollment client
    'msgUnreachable': 'Biatec OIDC je nedostupný: {why}',
    'msgRefused': 'Registrácia bola odmietnutá ({status}): {detail}',
    'msgBothKeys': 'Biatec OIDC nezaregistroval oba kľúče zariadenia.',
    'msgMalformed': 'Odpoveď na registráciu má chybný formát.',
    'msgUnexpected': 'Neočakávaná odpoveď ({status}).',
    'msgRecoveryUnavailable':
        'Obnovovací kód nie je k dispozícii ({status}): {detail}',
    'msgRecoveryAckFailed': 'Obnovovací kód sa nepodarilo potvrdiť ({status}).',
    'msgPinning':
        'Spojenie s {host} sa nepodarilo overiť, preto sa nič neodoslalo. Ak sa to opakuje, aktualizujte aplikáciu.',

    // The messages of the login clients
    'loginAlreadyOpen': 'Prihlásenie už prebieha.',
    'loginNoLoopback':
        'Táto zostava nemá lokálne presmerovanie (loopback), cez ktoré by sa dalo prihlásiť.',
    'loginNoBrowser': 'Prehliadač sa nepodarilo otvoriť.',
    'loginUnreachable': 'Poskytovateľ prihlásenia je nedostupný ({detail}).',
    'loginTimeout': 'Poskytovateľ prihlásenia neodpovedal včas.',
    'loginTls':
        'Spojenie s poskytovateľom prihlásenia sa nepodarilo overiť ({detail}).',
    'loginIncomplete': 'Prihlásenie sa nepodarilo dokončiť ({detail}).',
    'loginNoEndpoints': 'Táto zostava neuvádza žiadne prihlasovacie adresy.',
    'loginEndpointsHttps': 'Prihlasovacie adresy musia používať https.',
    'loginDiscoveryHttps': 'Adresa dokumentu discovery musí používať https.',
    'loginOtherIssuer': 'Dokument discovery uvádza iného vydavateľa.',
    'loginNoPkce': 'Poskytovateľ prihlásenia nepodporuje PKCE s S256.',
    'loginBadEndpoint':
        '{name} poskytovateľa prihlásenia nie je adresa https na {host}.',
    'loginPortInUse':
        'Port {port} na tomto počítači sa už používa. Zatvorte program, ktorý ho používa, a prihláste sa znova.',
    'loginPortFailed':
        'Port {port} na tomto počítači sa nedá otvoriť na prihlásenie ({detail}).',
    'loginProviderRefused':
        'Poskytovateľ prihlásenia prihlásenie odmietol ({code}).',
    'loginNotInTime': 'Prihlásenie sa nedokončilo včas.',
    'loginNoAccessToken': 'Poskytovateľ prihlásenia neposlal prístupový token.',
    'loginBadTokenType':
        'Poskytovateľ prihlásenia poslal token druhu, ktorý táto aplikácia nevie použiť.',
    'loginNoIdToken': 'Poskytovateľ prihlásenia neposlal ID token.',
    'loginIdUnreadable': 'ID token poskytovateľa prihlásenia sa nedá prečítať.',
    'loginIdNotMine': 'ID token nepatrí k tomuto prihláseniu.',
    'loginIdOtherIssuer': 'ID token je od iného vydavateľa.',
    'loginEarlierSignIn':
        'Poskytovateľ prihlásenia použil skoršie prihlásenie. Odhláste sa v prehliadači a prihláste sa znova.',
    'loginIdOtherApp': 'ID token je pre inú aplikáciu.',
    'loginNoAnswerWhat': 'Poskytovateľ prihlásenia neodpovedal včas ({what}).',
    'loginTooLarge':
        'Odpoveď poskytovateľa prihlásenia ({what}) je príliš veľká.',
    'loginRefusedWhat':
        'Poskytovateľ prihlásenia odmietol požiadavku ({what}; {status}, {code}).',
    'loginNotJson': 'Odpoveď poskytovateľa prihlásenia ({what}) nie je JSON.',
    'loginWhatDiscovery': 'dokument discovery',
    'loginWhatToken': 'odpoveď s tokenom',
    'loginIos':
        'Prihlásenie z tejto aplikácie vyžaduje iOS 17.4 alebo novší. Aktualizujte tento telefón alebo zariadenie zaregistrujte z novšieho.',
    'loginNoAccessTokenOidc': 'Biatec OIDC neposlal prístupový token.',

    // The pages the desktop sign-in shows in the browser
    'pageDone':
        'Prihlásenie bolo prijaté. Ak chcete pokračovať, vráťte sa do aplikácie Biatec Authenticator. Toto okno môžete zatvoriť.',
    'pageFailed':
        'Prihlásenie sa nedokončilo. Zatvorte toto okno a vráťte sa do aplikácie Biatec Authenticator.',
    'pageStray':
        'Túto adresu používa Biatec Authenticator počas prihlasovania. Nič sa nevykonalo.',
  });
}
