import 'dart:ui';

/// The texts of the in-app login and of the activation of a new device (T-242): English and Slovak, chosen by the phone's language (the onboarding screens have theirs in `OnboardingTexts`). The code that asks for a text keeps the keys, so a test can check that both languages have all of them.
class EnrollStrings {
  const EnrollStrings._(this._t);

  /// The strings for [locale] (the phone's language when null): Slovak for `sk`, English otherwise.
  factory EnrollStrings.of([Locale? locale]) {
    final language =
        (locale ?? PlatformDispatcher.instance.locale).languageCode;
    return language == 'sk' ? sk : en;
  }

  final Map<String, String> _t;

  String _get(String key) => _t[key]!;

  // Login
  String get signInIntro => _get('signInIntro');
  String get signInIntroPlain => _get('signInIntroPlain');
  String get noKeyStore => _get('noKeyStore');
  String get noKeyStoreTitle => _get('noKeyStoreTitle');
  String get noTpm => _get('noTpm');
  String get noTpmTitle => _get('noTpmTitle');

  // A Windows computer without a usable TPM: the keys are kept by the Windows account
  String get softwareKeyTitle => _get('softwareKeyTitle');
  String get softwareKey => _get('softwareKey');
  String get softwareKeyTips => _get('softwareKeyTips');
  String get softwareKeyShort => _get('softwareKeyShort');
  String get signInButton => _get('signInButton');
  String get signInAgainButton => _get('signInAgainButton');
  String get signInCancel => _get('signInCancel');
  String get signingIn => _get('signingIn');
  String get staleLoginNotice => _get('staleLoginNotice');
  String get staleLoginAgain => _get('staleLoginAgain');
  String get loginCancelled => _get('loginCancelled');
  String loginFailed(String why) => _get('loginFailed').replaceAll('{why}', why);
  String get signInUnavailable => _get('signInUnavailable');

  // Recovery code at enrollment
  String get recoveryFieldLabel => _get('recoveryFieldLabel');
  String get recoveryFieldHelp => _get('recoveryFieldHelp');
  String get recoveryWrongTitle => _get('recoveryWrongTitle');
  String get recoveryWrong => _get('recoveryWrong');
  String get recoveryLockedTitle => _get('recoveryLockedTitle');
  String get recoveryLocked => _get('recoveryLocked');
  String get recoveryFormat => _get('recoveryFormat');
  String get deviceCancelled => _get('deviceCancelled');

  // A new device waiting for another device
  String get pairingTitle => _get('pairingTitle');
  String get pairingHelp => _get('pairingHelp');
  String get waitingText => _get('waitingText');
  String get cancelThisDevice => _get('cancelThisDevice');
  String get cancelThisTitle => _get('cancelThisTitle');
  String get cancelThisMessage => _get('cancelThisMessage');
  String get cancelThisConfirm => _get('cancelThisConfirm');
  String get keep => _get('keep');

  // The Devices screen of an active device
  String activateTitle(String name) =>
      _get('activateTitle').replaceAll('{name}', name);
  String get newDeviceName => _get('newDeviceName');
  String get activateCodeLabel => _get('activateCodeLabel');
  String get activateCompare => _get('activateCompare');
  String get activateButton => _get('activateButton');
  String get activateConfirmTitle => _get('activateConfirmTitle');
  String get activateConfirmMessage => _get('activateConfirmMessage');
  String get activateConfirm => _get('activateConfirm');
  String get activatePrompt => _get('activatePrompt');
  String get activatePromptSubtitle => _get('activatePromptSubtitle');
  String activated(String name) => _get('activated').replaceAll('{name}', name);
  String get activateMismatch => _get('activateMismatch');
  String get activateUnavailable => _get('activateUnavailable');

  /// Every key, so a test can check that both languages have all of them, and the text under one.
  Iterable<String> get keys => _t.keys;

  /// The texts by key, for the test that both languages have the same keys and placeholders.
  Map<String, String> get values => _t;
  String text(String key) => _get(key);

  static const en = EnrollStrings._({
    'signInIntro':
        'Sign in to your Biatec account in the browser. You always sign in again, also if you were signed in before. Nothing from the sign-in is kept on this device.',
    'signInIntroPlain':
        'Sign in to your account in the browser. Nothing from the sign-in is kept on this device.',
    'noTpmTitle': 'No security chip found',
    'softwareKeyTitle': 'This computer is not the safest place for approvals',
    'softwareKey':
        'This computer has no security chip (TPM) that this app can use, so the keys of this device are kept by your Windows account. They cannot be exported, but a program that runs in your account, or anyone who can sign in to it, could use them. A phone, or a computer with a TPM, protects them better.',
    'softwareKeyTips': 'What makes this computer safer:\n'
        '• Turn on the TPM in the firmware settings of the computer (often called Intel PTT or AMD fTPM). Then remove this device from your account and enroll it again.\n'
        '• Set up Windows Hello (Settings › Accounts › Sign-in options).\n'
        '• Turn on device encryption (BitLocker), so the keys cannot be read from a stolen disk.\n'
        '• Use a strong Windows password, lock the screen when you leave, and share this Windows account with nobody.\n'
        '• Keep Windows and its virus protection up to date.\n'
        '• Approve larger payments on a phone with Biatec Authenticator.',
    'softwareKeyShort': 'Kept by your Windows account (software), not by a security chip. It cannot be exported.',
    'noTpm':
        'This computer has no TPM security chip that can hold the keys of this device, or it is turned off in the firmware. Turn it on, or use another computer. Nothing was enrolled.',
    'noKeyStoreTitle': 'This device cannot be enrolled',
    'noKeyStore':
        'This build of the app has no protected key store on this kind of device, so the device cannot be enrolled here. Nothing was enrolled.',
    'signInButton': 'Sign in with Biatec',
    'signInAgainButton': 'Sign in again',
    'signInCancel': 'Stop waiting',
    'signingIn': 'Waiting for the browser...',
    'staleLoginNotice': 'That sign-in was too old. Sign in again.',
    'staleLoginAgain':
        'Biatec OIDC still says the sign-in is too old. Check the clock of this phone and sign in again.',
    'loginCancelled': 'Sign-in was cancelled. Nothing was enrolled.',
    'loginFailed': 'Sign-in failed: {why}',
    'signInUnavailable':
        'This Biatec OIDC does not support signing in from the app yet, so this device cannot be enrolled here.',
    'recoveryFieldLabel': 'Recovery code (optional)',
    'recoveryFieldHelp':
        'Replacing a lost phone? Enter your recovery code and this device is activated at once.',
    'recoveryWrongTitle': 'Recovery code not accepted',
    'recoveryWrong':
        'That recovery code is not right. Check it and try again. Nothing was enrolled.',
    'recoveryFormat':
        'A recovery code has three groups of four letters or digits, like 1A2B-3C4D-5E6F. Check it: nothing was sent.',
    'deviceCancelled':
        'This device was cancelled, from the email link or from another phone. It was removed from this phone; enroll it again if you want to use it.',
    'recoveryLockedTitle': 'Too many attempts',
    'recoveryLocked':
        'Too many wrong recovery codes. Wait a while before you try again, or leave the field empty and activate this device from another phone.',
    'pairingTitle': 'Pairing code',
    'pairingHelp':
        'On a phone that is already set up, open Devices and activate this device. Activate it only if that phone shows the same code.',
    'waitingText':
        'If nobody activates this device, it becomes active after 24 hours. An email with a link to cancel it was sent to the address of your account.',
    'cancelThisDevice': 'Cancel this device',
    'cancelThisTitle': 'Cancel this device?',
    'cancelThisMessage':
        'This device is removed from your account and you will have to enroll it again from the start.',
    'cancelThisConfirm': 'Cancel this device',
    'keep': 'Keep',
    'activateTitle': 'Activate {name}?',
    'newDeviceName': 'a new device',
    'activateCodeLabel': 'Pairing code',
    'activateCompare':
        'Compare it with the code on the new device. Activate only if both phones show the same code. If they differ, or you did not start this, cancel the device.',
    'activateButton': 'Activate',
    'activateConfirmTitle': 'Do the codes match?',
    'activateConfirmMessage':
        'Both phones must show the same code. Your fingerprint or face is asked next.',
    'activateConfirm': 'The codes match',
    'activatePrompt': 'Activate a new device',
    'activatePromptSubtitle': 'It will be able to approve for your account',
    'activated': '{name} is active.',
    'activateMismatch':
        'The pairing code from Biatec OIDC does not match the keys of that device. It was not activated: cancel it.',
    'activateUnavailable':
        'This device cannot activate others: it needs its approval key and attestation.',
  });

  static const sk = EnrollStrings._({
    'signInIntro':
        'Prihláste sa do svojho účtu Biatec v prehliadači. Prihlásite sa vždy znova, aj keď ste už boli prihlásený. Z prihlásenia sa v tomto zariadení nič neukladá.',
    'signInIntroPlain':
        'Prihláste sa do svojho účtu v prehliadači. Z prihlásenia sa v tomto zariadení nič neukladá.',
    'noTpmTitle': 'Bezpečnostný čip sa nenašiel',
    'softwareKeyTitle': 'Tento počítač nie je najbezpečnejšie miesto na schvaľovanie',
    'softwareKey':
        'Tento počítač nemá bezpečnostný čip (TPM), ktorý by táto aplikácia mohla použiť, preto kľúče tohto zariadenia uchováva váš účet vo Windows. Nedajú sa exportovať, ale mohol by ich použiť program spustený vo vašom účte alebo ktokoľvek, kto sa doň dokáže prihlásiť. Telefón alebo počítač s TPM ich chráni lepšie.',
    'softwareKeyTips': 'Čo urobí tento počítač bezpečnejším:\n'
        '• Zapnite TPM v nastaveniach firmvéru počítača (často sa volá Intel PTT alebo AMD fTPM). Potom toto zariadenie odstráňte zo svojho účtu a zaregistrujte ho znova.\n'
        '• Nastavte si Windows Hello (Nastavenia › Kontá › Možnosti prihlásenia).\n'
        '• Zapnite šifrovanie zariadenia (BitLocker), aby sa kľúče nedali prečítať z ukradnutého disku.\n'
        '• Používajte silné heslo do Windows, pri odchode zamknite obrazovku a tento účet vo Windows s nikým nezdieľajte.\n'
        '• Udržiavajte Windows a antivírusovú ochranu aktuálne.\n'
        '• Väčšie platby schvaľujte na telefóne s aplikáciou Biatec Authenticator.',
    'softwareKeyShort': 'Uchováva ho váš účet vo Windows (softvér), nie bezpečnostný čip. Nedá sa exportovať.',
    'noTpm':
        'Tento počítač nemá bezpečnostný čip TPM, ktorý by mohol uchovať kľúče tohto zariadenia, alebo je vo firmvéri vypnutý. Zapnite ho alebo použite iný počítač. Nič sa nezaregistrovalo.',
    'noKeyStoreTitle': 'Toto zariadenie sa nedá zaregistrovať',
    'noKeyStore':
        'Táto zostava aplikácie nemá na tomto type zariadenia chránené úložisko kľúčov, preto sa tu zariadenie nedá zaregistrovať. Nič sa nezaregistrovalo.',
    'signInButton': 'Prihlásiť sa cez Biatec',
    'signInAgainButton': 'Prihlásiť sa znova',
    'signInCancel': 'Prestať čakať',
    'signingIn': 'Čakám na prehliadač...',
    'staleLoginNotice': 'Prihlásenie bolo príliš staré. Prihláste sa znova.',
    'staleLoginAgain':
        'Biatec OIDC stále hlási, že prihlásenie je príliš staré. Skontrolujte hodiny telefónu a prihláste sa znova.',
    'loginCancelled': 'Prihlásenie bolo zrušené. Nič sa nezaregistrovalo.',
    'loginFailed': 'Prihlásenie zlyhalo: {why}',
    'signInUnavailable':
        'Tento Biatec OIDC zatiaľ nepodporuje prihlásenie z aplikácie, preto sa toto zariadenie tu nemôže zaregistrovať.',
    'recoveryFieldLabel': 'Obnovovací kód (voliteľné)',
    'recoveryFieldHelp':
        'Nahrádzate stratený telefón? Zadajte obnovovací kód a toto zariadenie sa aktivuje hneď.',
    'recoveryWrongTitle': 'Obnovovací kód nebol prijatý',
    'recoveryWrong':
        'Obnovovací kód nie je správny. Skontrolujte ho a skúste znova. Nič sa nezaregistrovalo.',
    'recoveryFormat':
        'Obnovovací kód má tri skupiny po štyri znaky, napríklad 1A2B-3C4D-5E6F. Skontrolujte ho: nič sa neodoslalo.',
    'deviceCancelled':
        'Toto zariadenie bolo zrušené, cez odkaz v e-maile alebo z iného telefónu. Z tohto telefónu bolo odstránené; ak ho chcete používať, zaregistrujte ho znova.',
    'recoveryLockedTitle': 'Príliš veľa pokusov',
    'recoveryLocked':
        'Príliš veľa nesprávnych obnovovacích kódov. Chvíľu počkajte, alebo pole nechajte prázdne a aktivujte toto zariadenie z iného telefónu.',
    'pairingTitle': 'Párovací kód',
    'pairingHelp':
        'Na telefóne, ktorý je už nastavený, otvorte Zariadenia a aktivujte toto zariadenie. Aktivujte ho, len ak ten telefón ukazuje rovnaký kód.',
    'waitingText':
        'Ak toto zariadenie nikto neaktivuje, aktivuje sa po 24 hodinách. Na adresu vášho účtu sme poslali e-mail s odkazom na jeho zrušenie.',
    'cancelThisDevice': 'Zrušiť toto zariadenie',
    'cancelThisTitle': 'Zrušiť toto zariadenie?',
    'cancelThisMessage':
        'Zariadenie sa odstráni z vášho účtu a budete ho musieť zaregistrovať odznova.',
    'cancelThisConfirm': 'Zrušiť zariadenie',
    'keep': 'Ponechať',
    'activateTitle': 'Aktivovať {name}?',
    'newDeviceName': 'nové zariadenie',
    'activateCodeLabel': 'Párovací kód',
    'activateCompare':
        'Porovnajte ho s kódom na novom zariadení. Aktivujte, len ak oba telefóny ukazujú rovnaký kód. Ak sa líšia, alebo ste to nezahájili vy, zariadenie zrušte.',
    'activateButton': 'Aktivovať',
    'activateConfirmTitle': 'Zhodujú sa kódy?',
    'activateConfirmMessage':
        'Oba telefóny musia ukazovať rovnaký kód. Potom sa vypýta odtlačok prsta alebo tvár.',
    'activateConfirm': 'Kódy sa zhodujú',
    'activatePrompt': 'Aktivovať nové zariadenie',
    'activatePromptSubtitle': 'Bude môcť schvaľovať za váš účet',
    'activated': '{name} je aktívne.',
    'activateMismatch':
        'Párovací kód z Biatec OIDC nesedí s kľúčmi toho zariadenia. Nebolo aktivované: zrušte ho.',
    'activateUnavailable':
        'Toto zariadenie nemôže aktivovať iné: potrebuje svoj schvaľovací kľúč a atestáciu.',
  });
}
