import 'dart:ui';

/// The texts of the in-app login and of the activation of a new device (T-242): English and Slovak, chosen by the phone's language (the rest of the app is English only for now). The code that asks for a text keeps the keys, so a test can check that both languages have all of them.
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
  String get signInButton => _get('signInButton');
  String get signInAgainButton => _get('signInAgainButton');
  String get signingIn => _get('signingIn');
  String get staleLoginNotice => _get('staleLoginNotice');
  String get staleLoginAgain => _get('staleLoginAgain');
  String get loginCancelled => _get('loginCancelled');
  String loginFailed(String why) => _get('loginFailed').replaceAll('{why}', why);
  String get signInUnavailableTitle => _get('signInUnavailableTitle');
  String get signInUnavailable => _get('signInUnavailable');

  // Recovery code at enrollment
  String get recoveryFieldLabel => _get('recoveryFieldLabel');
  String get recoveryFieldHelp => _get('recoveryFieldHelp');
  String get recoveryWrongTitle => _get('recoveryWrongTitle');
  String get recoveryWrong => _get('recoveryWrong');
  String get recoveryLockedTitle => _get('recoveryLockedTitle');
  String get recoveryLocked => _get('recoveryLocked');

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
  String text(String key) => _get(key);

  static const en = EnrollStrings._({
    'signInIntro':
        'Sign in to your Biatec account in the browser. You always sign in again, also if you were signed in before. Nothing from the sign-in is kept on this phone.',
    'signInButton': 'Sign in with Biatec',
    'signInAgainButton': 'Sign in again',
    'signingIn': 'Waiting for the browser...',
    'staleLoginNotice': 'That sign-in was too old. Sign in again.',
    'staleLoginAgain':
        'Biatec OIDC still says the sign-in is too old. Check the clock of this phone and sign in again.',
    'loginCancelled': 'Sign-in was cancelled. Nothing was enrolled.',
    'loginFailed': 'Sign-in failed: {why}',
    'signInUnavailableTitle': 'Sign-in is not available yet',
    'signInUnavailable':
        'This Biatec OIDC does not support signing in from the app yet, so this device cannot be enrolled here.',
    'recoveryFieldLabel': 'Recovery code (optional)',
    'recoveryFieldHelp':
        'Replacing a lost phone? Enter your recovery code and this device is activated at once.',
    'recoveryWrongTitle': 'Recovery code not accepted',
    'recoveryWrong':
        'That recovery code is not right. Check it and try again. Nothing was enrolled.',
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
        'Prihláste sa do svojho účtu Biatec v prehliadači. Prihlásite sa vždy znova, aj keď ste už boli prihlásený. Z prihlásenia sa v tomto telefóne nič neukladá.',
    'signInButton': 'Prihlásiť sa cez Biatec',
    'signInAgainButton': 'Prihlásiť sa znova',
    'signingIn': 'Čakám na prehliadač...',
    'staleLoginNotice': 'Prihlásenie bolo príliš staré. Prihláste sa znova.',
    'staleLoginAgain':
        'Biatec OIDC stále hlási, že prihlásenie je príliš staré. Skontrolujte hodiny telefónu a prihláste sa znova.',
    'loginCancelled': 'Prihlásenie bolo zrušené. Nič sa nezaregistrovalo.',
    'loginFailed': 'Prihlásenie zlyhalo: {why}',
    'signInUnavailableTitle': 'Prihlásenie zatiaľ nie je dostupné',
    'signInUnavailable':
        'Tento Biatec OIDC zatiaľ nepodporuje prihlásenie z aplikácie, preto sa toto zariadenie tu nemôže zaregistrovať.',
    'recoveryFieldLabel': 'Obnovovací kód (voliteľné)',
    'recoveryFieldHelp':
        'Nahradízate stratený telefón? Zadajte obnovovací kód a toto zariadenie sa aktivuje hneď.',
    'recoveryWrongTitle': 'Obnovovací kód nebol prijatý',
    'recoveryWrong':
        'Obnovovací kód nie je správny. Skontrolujte ho a skúste znova. Nič sa nezaregistrovalo.',
    'recoveryLockedTitle': 'Príliš veľa pokusov',
    'recoveryLocked':
        'Príliš veľa nesprávnych obnovovacích kódov. Chvíľu počkajte, alebo pole nechajte prázdne a aktivujte toto zariadenie z iného telefónu.',
    'pairingTitle': 'Párovací kód',
    'pairingHelp':
        'Na telefóne, ktorý je už nastavený, otvorte Zariadenia a aktivujte toto zariadenie. Aktivujte ho, len ak ten telefón ukazuje rovnaký kód.',
    'waitingText':
        'Ak toto zariadenie nikto neaktivuje, aktivuje sa po 24 hodinách. Na adresu vašho účtu sme poslali e-mail s odkazom na jeho zrušenie.',
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
