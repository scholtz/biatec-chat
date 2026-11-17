import 'package:flutter/widgets.dart' show Locale, WidgetsBinding;

import '../l10n/texts.dart';

/// The device's language as the app's binding reports it, or null before there is a binding (then [appLanguageIsSlovak] asks the platform). In the app
/// it is the platform's language; a test sets it with `tester.platformDispatcher.localeTestValue`, which `PlatformDispatcher.instance` does not see.
Locale? bindingLocale() {
  try {
    return WidgetsBinding.instance.platformDispatcher.locale;
  } on Object {
    return null;
  }
}

/// The messages of the two-factor engine that a screen shows (T-251): why a setup link, a key or a name was refused, and why a backup was not made or
/// read. The engine has no `BuildContext`, so it asks the device's language, the same one the screens use.
class TotpTexts extends Texts {
  const TotpTexts._(super.values);

  factory TotpTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale ?? bindingLocale()) ? sk : en;

  static const en = TotpTexts._({
    // The setup link (otpauth://)
    'tooLong': 'This link is too long to be a setup link.',
    'notOtpAuth': 'This is not a setup link for a code.',
    'unsupportedType': 'Only time-based and counter-based codes are supported.',
    'invalidEncoding': 'This link has a setting that cannot be read.',
    'duplicateParameter': 'This link sets the same value twice.',
    'missingSecret': 'This link has no secret key.',
    'invalidAlgorithm':
        'This link uses a hash that is not supported (SHA1, SHA256 and SHA512 are).',
    'missingCounter': 'A counter-based account needs its starting counter.',
    'invalidCounter': 'The counter in this link is not valid.',
    'invalidSecret': 'The secret key in this link is not valid.',
    'invalidName': 'The name in this link is not valid.',
    // The rules of an account
    'digits': 'Codes must have {min} to {max} digits.',
    'period': 'A code must last between {min} and {max} seconds.',
    'weakSecret':
        'The secret key is too short to be safe (at least {bits} bits).',
    'counter': 'The counter must be a whole number from 0 to {max}.',
    'totpNoCounter': 'A time-based account does not have a counter.',
    'hotpNoPeriod': 'A counter-based account does not have a period.',
    'noName': 'This account has no name.',
    'badName':
        'The name is too long or has invisible or direction-changing characters.',
    'nameNotAllowed': 'This name is not allowed.',
    // A typed setup key (Base32)
    'b32BrokenPadding': 'The key has broken-up padding.',
    'b32BadCharacter':
        'The key has a character that is not part of Base32 (letters A to Z and digits 2 to 7).',
    'b32MiddlePadding': 'The key has padding in the middle.',
    'b32Empty': 'The key is empty.',
    'b32WrongPadding': 'The key has the wrong padding.',
    'b32TooShort': 'The key is too short.',
    // The backup
    'backupFailure':
        'This backup could not be opened. Check the passphrase and the file.',
    'exportReason': 'Create a backup of your accounts',
    'restoreReason': 'Restore accounts from a backup',
    'exportAuthFailed':
        'The fingerprint or face check did not pass. No backup was made.',
    'nothingToBackUp': 'There are no accounts to back up yet.',
    'shareClosed':
        'The share sheet was closed before the file was saved. No backup was made.',
    'readAuthFailed':
        'The fingerprint or face check did not pass. Nothing was read.',
    'nothingToRestore': 'This backup has no accounts to restore.',
    'fileType': 'Biatec Authenticator backup',
    // The passphrase
    'passTooShort':
        'Use at least {min} characters. A phrase of several words is easy to remember and hard to guess.',
    'passTooEasy':
        'This is too easy to guess: it repeats, runs on, or is built from a common word. Try several unrelated words, or a longer phrase.',
    'passMismatch': 'The two entries are not the same.',
  });

  static const sk = TotpTexts._({
    'tooLong':
        'Tento odkaz je príliš dlhý na to, aby bol odkazom na nastavenie.',
    'notOtpAuth': 'Toto nie je odkaz na nastavenie kódu.',
    'unsupportedType': 'Podporované sú len časové kódy a kódy podľa počítadla.',
    'invalidEncoding': 'Tento odkaz má nastavenie, ktoré sa nedá prečítať.',
    'duplicateParameter': 'Tento odkaz nastavuje tú istú hodnotu dvakrát.',
    'missingSecret': 'Tento odkaz nemá tajný kľúč.',
    'invalidAlgorithm':
        'Tento odkaz používa nepodporovanú hašovaciu funkciu (podporované sú SHA1, SHA256 a SHA512).',
    'missingCounter':
        'Účet podľa počítadla potrebuje počiatočnú hodnotu počítadla.',
    'invalidCounter': 'Počítadlo v tomto odkaze nie je platné.',
    'invalidSecret': 'Tajný kľúč v tomto odkaze nie je platný.',
    'invalidName': 'Názov v tomto odkaze nie je platný.',
    'digits': 'Kódy musia mať {min} až {max} číslic.',
    'period': 'Kód musí platiť {min} až {max} sekúnd.',
    'weakSecret':
        'Tajný kľúč je príliš krátky na to, aby bol bezpečný (najmenej {bits} bitov).',
    'counter': 'Počítadlo musí byť celé číslo od 0 do {max}.',
    'totpNoCounter': 'Časový účet nemá počítadlo.',
    'hotpNoPeriod': 'Účet podľa počítadla nemá periódu.',
    'noName': 'Tento účet nemá názov.',
    'badName':
        'Názov je príliš dlhý alebo obsahuje neviditeľné znaky či znaky meniace smer textu.',
    'nameNotAllowed': 'Tento názov nie je povolený.',
    'b32BrokenPadding': 'Kľúč má prerušené doplnenie (padding).',
    'b32BadCharacter':
        'Kľúč obsahuje znak, ktorý nepatrí do Base32 (písmená A až Z a číslice 2 až 7).',
    'b32MiddlePadding': 'Kľúč má doplnenie (padding) v strede.',
    'b32Empty': 'Kľúč je prázdny.',
    'b32WrongPadding': 'Kľúč má nesprávne doplnenie (padding).',
    'b32TooShort': 'Kľúč je príliš krátky.',
    'backupFailure':
        'Túto zálohu sa nepodarilo otvoriť. Skontrolujte prístupovú frázu a súbor.',
    'exportReason': 'Vytvoriť zálohu vašich účtov',
    'restoreReason': 'Obnoviť účty zo zálohy',
    'exportAuthFailed':
        'Overenie odtlačkom prsta alebo tvárou neprešlo. Záloha sa nevytvorila.',
    'nothingToBackUp': 'Zatiaľ nemáte žiadne účty na zálohovanie.',
    'shareClosed':
        'Ponuka zdieľania sa zavrela skôr, ako sa súbor uložil. Záloha sa nevytvorila.',
    'readAuthFailed':
        'Overenie odtlačkom prsta alebo tvárou neprešlo. Nič sa neprečítalo.',
    'nothingToRestore': 'Táto záloha neobsahuje žiadne účty na obnovenie.',
    'fileType': 'Záloha Biatec Authenticator',
    'passTooShort':
        'Použite najmenej {min} znakov. Fráza z niekoľkých slov sa ľahko pamätá a ťažko uhádne.',
    'passTooEasy':
        'Táto fráza sa dá príliš ľahko uhádnuť: opakuje sa, ide po sebe alebo je zložená z bežného slova. Skúste niekoľko nesúvisiacich slov alebo dlhšiu frázu.',
    'passMismatch': 'Zadané frázy sa nezhodujú.',
  });
}
