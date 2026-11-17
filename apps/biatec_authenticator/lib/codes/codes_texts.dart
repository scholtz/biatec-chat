import 'package:flutter/widgets.dart' show BuildContext, Locale, Localizations;

import '../l10n/texts.dart';
import '../totp/totp_texts.dart' show bindingLocale;

/// The texts of the Codes tab (T-251): the list and its tiles, adding an account, the QR scan, and backup and restore. English and Slovak.
class CodesTexts extends Texts {
  const CodesTexts._(super.values);

  /// The texts for [locale], or the device's language when it is null (the controller, which has no `BuildContext`).
  factory CodesTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale ?? bindingLocale()) ? sk : en;

  /// The texts in the language of the widget tree.
  factory CodesTexts.from(BuildContext context) =>
      CodesTexts.of(Localizations.maybeLocaleOf(context));

  /// A text with a count [n]: the key with `One` (1), `Few` (2 to 4) or `Many` after it, as Slovak needs; `{n}` is filled in.
  String count(String key, int n, [Map<String, Object?> args = const {}]) {
    final form = n == 1
        ? 'One'
        : n >= 2 && n <= 4
            ? 'Few'
            : 'Many';
    return text('$key$form', {...args, 'n': n});
  }

  static const en = CodesTexts._({
    // The list
    'codesTitle': 'Codes',
    'searchHint': 'Search accounts',
    'search': 'Search',
    'closeSearch': 'Close search',
    'backupAndRestore': 'Backup and restore',
    'showWithoutTap': 'Show codes without tapping',
    'hideUntilTap': 'Hide codes until tapped',
    'addAccount': 'Add account',
    'copied': 'Code copied. It is cleared from the clipboard in 30 seconds.',
    'changeNotSaved': 'That could not be saved. Nothing was changed.',
    'deleteTitle': 'Delete this account?',
    'deleteMessage':
        '{name} will be removed from this phone. You will not get its codes here any more, and the key cannot be shown again. '
            'If you still need to sign in with it, set it up again at the service first.',
    'deleteWarning': 'This cannot be undone.',
    'delete': 'Delete',
    'keep': 'Keep',
    'deleteAuthFailed':
        'The fingerprint or face check did not pass. Nothing was deleted.',
    'openFailedTitle': 'Your codes could not be opened',
    'tryAgain': 'Try again.',
    'retry': 'Try again',
    'clockResetTitle': 'Your phone\'s clock looks wrong',
    'clockResetMessage':
        'The date is earlier than one this app has already seen, so time-based codes would be wrong and are hidden. '
            'Turn on automatic date and time in your phone\'s settings.',
    'clockOffTitle': 'Your phone\'s clock is off',
    'clockOffMessage':
        'It is {offBy} from the server. Codes made from a wrong clock are refused by the service. '
            'Turn on automatic date and time in your phone\'s settings.',
    'offAhead': '{how} ahead',
    'offBehind': '{how} behind',
    'minutesOne': '{n} minute',
    'minutesFew': '{n} minutes',
    'minutesMany': '{n} minutes',
    'secondsOne': '{n} second',
    'secondsFew': '{n} seconds',
    'secondsMany': '{n} seconds',
    'noMatch': 'No account matches "{query}".',
    'noCodes': 'No codes can be shown.',
    'emptyTitle': 'No codes yet',
    'emptyText':
        'Add an account from any service that asks for an authenticator app. Pick the way it gives you the account.',
    'scanQr': 'Scan a QR code',
    'pasteLink': 'Paste a link',
    'enterKey': 'Enter a setup key',
    'renewIn10': 'Codes renew in 10 seconds',
    'renewIn5': 'Codes renew in 5 seconds',
    'renewed': 'Codes renewed',
    // Renaming
    'rename': 'Rename',
    'service': 'Service',
    'accountName': 'Account name',
    'save': 'Save',
    'cancel': 'Cancel',
    'renameFailed': 'The name could not be saved. Try again.',
    // The tile
    'spokenHidden': '{name}. Code hidden. Double tap to show it.',
    'spokenCode': '{name}. Code {code}. Double tap to copy.',
    'next': 'Next: {code}',
    'nextCodeFor': 'Next code for {name}',
    'nextCode': 'Next code',
    'moreFor': 'More for {name}',
    'moveUp': 'Move up',
    'moveDown': 'Move down',
    // The controller
    'biometricsChanged':
        'Your fingerprints or face changed, so the codes cannot be opened.',
    'authFailedTryAgain':
        'The fingerprint or face check did not pass. Try again.',
    'rolledBack':
        'The saved accounts are older than what this phone has seen, so they are not opened. Restore them from your backup file.',
    'corrupt':
        'The saved accounts are damaged, so they are not opened. Restore them from your backup file.',
    'openFailed': 'The saved accounts could not be opened. Try again.',
    'addWhileFailed':
        'Your saved accounts could not be opened, so nothing can be added. Try opening them again first.',
    'duplicate': 'You already have this account: {who}.',
    'lockedWhileAdding':
        'The app locked before the account was shown. It was saved; open the list again.',
    'deleteReason': 'Delete this account',
    // Adding an account
    'addTitle': 'Add an account',
    'checkTitle': 'Check the account',
    'notAdded': 'Not added',
    'qrNotAccount': 'That QR code is not a two-factor account. {why}',
    'saveAuthFailed':
        'The fingerprint or face check did not pass. Nothing was saved.',
    'saveBiometricsChanged':
        'Your fingerprints or face changed. Nothing was saved.',
    'saveFailed': 'The account could not be saved. Try again.',
    'pickWay': 'Pick the way your service gives you the account.',
    'scanText': 'The camera is asked for only now. No picture is kept.',
    'linkText': 'A link that starts with otpauth://',
    'keyText': 'The letters and digits the service shows next to the QR code.',
    'linkLabel': 'otpauth:// link',
    'linkHelp': 'Paste the whole link.',
    'continue': 'Continue',
    'serviceHelp': 'For example GitHub.',
    'accountNameHelp': 'Usually your email address.',
    'setupKey': 'Setup key',
    'setupKeyHelp': 'Spaces and capital letters do not matter.',
    'moreSettings': 'More settings',
    'moreSettingsHelp': 'Only change these if the service says so.',
    'algorithm': 'Algorithm',
    'digits': 'Digits',
    'period': 'Seconds each code lasts',
    'noServiceName': 'No service name',
    'counterBased': 'Counter-based, starting at {counter}',
    'timeBased': 'Time-based, a new code every {period} seconds',
    'digitsLine': '{digits} digits, {algorithm}',
    'keyStrength': 'Key strength: {bits} bits',
    'checkText':
        'Check that this is the account you meant. Once it is saved the key is never shown again.',
    'saving': 'Saving',
    // The QR scan
    'scanHelp':
        'Point the camera at the QR code the service shows. Nothing is recorded.',
    'cameraUnavailable': 'The camera is not available',
    'cameraDenied':
        'Allow the camera for this app in your phone\'s settings, or paste the link or type the setup key instead.',
    'cameraOther': 'You can paste the link or type the setup key instead.',
    // Backup and restore
    'backupTitle': 'Backup',
    'backupIntro':
        'A backup is a file with all your two-factor accounts, locked with a passphrase you choose. Keep it somewhere safe. '
            'Nothing is sent to any server, and without the passphrase the file is useless: a lost passphrase cannot be recovered.',
    'createBackup': 'Create a backup',
    'createBackupText': 'Asks for your fingerprint or face, then a passphrase.',
    'restoreTitle': 'Restore from a backup',
    'restoreText':
        'Adds the accounts of a backup file. Accounts you already have are skipped.',
    'strengthRefused': 'Too weak',
    'strengthFair': 'Fair',
    'strengthGood': 'Good',
    'strengthStrong': 'Strong',
    'strength': 'Strength: {word}',
    'passphrase': 'Passphrase',
    'passphraseHelp':
        'At least 12 characters. A phrase of several words is best.',
    'hidePassphrase': 'Hide the passphrase',
    'showPassphrase': 'Show the passphrase',
    'typeAgain': 'Type it again',
    'passphraseMismatch': 'The two entries are not the same.',
    'backupFailed':
        'The backup could not be made. Nothing was saved. Try again.',
    'noRecoveryTitle': 'The passphrase cannot be recovered',
    'noRecovery':
        'It is not stored anywhere. If you lose it, the backup cannot be opened by anyone, including us.',
    'noBackupMade': 'No backup was made',
    'backupReady': 'Backup ready',
    'backupDoneOne':
        '{n} account is in the file. Save it where only you can reach it, and keep the passphrase apart from it.',
    'backupDoneFew':
        '{n} accounts are in the file. Save it where only you can reach it, and keep the passphrase apart from it.',
    'backupDoneMany':
        '{n} accounts are in the file. Save it where only you can reach it, and keep the passphrase apart from it.',
    'protecting':
        'Protecting the backup. This takes a few seconds on purpose: it is what makes the passphrase hard to guess.',
    'working': 'Working',
    'createTheBackup': 'Create the backup',
    'readFailed': 'The file could not be read. Nothing was added.',
    'addAllFailed':
        'The accounts could not all be added. Open the list to see which are there, and try again.',
    'afterBiometrics':
        'The accounts that were on this phone cannot be opened any more. Restoring a backup replaces them with the accounts in the file.',
    'nothingAdded': 'Nothing was added',
    'restored': 'Restored',
    'restoredOne': '{n} account was added.',
    'restoredFew': '{n} accounts were added.',
    'restoredMany': '{n} accounts were added.',
    'chooseFile': 'Choose the backup file',
    'nothingNew': 'Nothing new in this backup',
    'willAddOne': 'This account will be added',
    'willAddFew': 'These {n} accounts will be added',
    'willAddMany': 'These {n} accounts will be added',
    'duplicatesOne': '{n} account is already here and will be skipped.',
    'duplicatesFew': '{n} accounts are already here and will be skipped.',
    'duplicatesMany': '{n} accounts are already here and will be skipped.',
    'restoreThese': 'Restore these accounts',
    'addThese': 'Add these accounts',
  });

  static const sk = CodesTexts._({
    'codesTitle': 'Kódy',
    'searchHint': 'Hľadať účty',
    'search': 'Hľadať',
    'closeSearch': 'Zavrieť hľadanie',
    'backupAndRestore': 'Záloha a obnova',
    'showWithoutTap': 'Zobrazovať kódy bez ťuknutia',
    'hideUntilTap': 'Skrývať kódy do ťuknutia',
    'addAccount': 'Pridať účet',
    'copied': 'Kód je skopírovaný. Zo schránky sa vymaže o 30 sekúnd.',
    'changeNotSaved': 'Zmenu sa nepodarilo uložiť. Nič sa nezmenilo.',
    'deleteTitle': 'Vymazať tento účet?',
    'deleteMessage':
        '{name} sa z tohto telefónu odstráni. Jeho kódy tu už nedostanete a kľúč sa už nedá znova zobraziť. '
            'Ak sa s ním ešte potrebujete prihlasovať, najprv ho znova nastavte v danej službe.',
    'deleteWarning': 'Toto sa nedá vrátiť späť.',
    'delete': 'Vymazať',
    'keep': 'Ponechať',
    'deleteAuthFailed':
        'Overenie odtlačkom prsta alebo tvárou neprešlo. Nič sa nevymazalo.',
    'openFailedTitle': 'Vaše kódy sa nepodarilo otvoriť',
    'tryAgain': 'Skúste to znova.',
    'retry': 'Skúsiť znova',
    'clockResetTitle': 'Hodiny vášho telefónu sú zrejme nesprávne',
    'clockResetMessage':
        'Dátum je skorší ako dátum, ktorý už táto aplikácia videla, preto by časové kódy boli nesprávne a sú skryté. '
            'V nastaveniach telefónu zapnite automatický dátum a čas.',
    'clockOffTitle': 'Hodiny vášho telefónu nesedia',
    'clockOffMessage':
        'Oproti serveru {offBy}. Kódy vytvorené podľa nesprávnych hodín služba odmietne. '
            'V nastaveniach telefónu zapnite automatický dátum a čas.',
    'offAhead': 'idú napred o {how}',
    'offBehind': 'meškajú o {how}',
    'minutesOne': '{n} minútu',
    'minutesFew': '{n} minúty',
    'minutesMany': '{n} minút',
    'secondsOne': '{n} sekundu',
    'secondsFew': '{n} sekundy',
    'secondsMany': '{n} sekúnd',
    'noMatch': 'Hľadaniu „{query}“ nezodpovedá žiadny účet.',
    'noCodes': 'Nedajú sa zobraziť žiadne kódy.',
    'emptyTitle': 'Zatiaľ žiadne kódy',
    'emptyText':
        'Pridajte účet z ktorejkoľvek služby, ktorá žiada overovaciu aplikáciu. Vyberte spôsob, akým vám služba účet poskytuje.',
    'scanQr': 'Naskenovať QR kód',
    'pasteLink': 'Vložiť odkaz',
    'enterKey': 'Zadať kľúč na nastavenie',
    'renewIn10': 'Kódy sa obnovia o 10 sekúnd',
    'renewIn5': 'Kódy sa obnovia o 5 sekúnd',
    'renewed': 'Kódy sa obnovili',
    'rename': 'Premenovať',
    'service': 'Služba',
    'accountName': 'Názov účtu',
    'save': 'Uložiť',
    'cancel': 'Zrušiť',
    'renameFailed': 'Názov sa nepodarilo uložiť. Skúste to znova.',
    'spokenHidden': '{name}. Kód je skrytý. Dvojitým ťuknutím ho zobrazíte.',
    'spokenCode': '{name}. Kód {code}. Dvojitým ťuknutím ho skopírujete.',
    'next': 'Ďalší: {code}',
    'nextCodeFor': 'Ďalší kód pre {name}',
    'nextCode': 'Ďalší kód',
    'moreFor': 'Ďalšie možnosti pre {name}',
    'moveUp': 'Posunúť vyššie',
    'moveDown': 'Posunúť nižšie',
    'biometricsChanged':
        'Vaše odtlačky prstov alebo tvár sa zmenili, preto sa kódy nedajú otvoriť.',
    'authFailedTryAgain':
        'Overenie odtlačkom prsta alebo tvárou neprešlo. Skúste to znova.',
    'rolledBack':
        'Uložené účty sú staršie ako tie, ktoré už tento telefón videl, preto sa neotvoria. Obnovte ich zo záložného súboru.',
    'corrupt':
        'Uložené účty sú poškodené, preto sa neotvoria. Obnovte ich zo záložného súboru.',
    'openFailed': 'Uložené účty sa nepodarilo otvoriť. Skúste to znova.',
    'addWhileFailed':
        'Vaše uložené účty sa nepodarilo otvoriť, preto sa nedá nič pridať. Najprv ich skúste otvoriť znova.',
    'duplicate': 'Tento účet už máte: {who}.',
    'lockedWhileAdding':
        'Aplikácia sa zamkla skôr, ako sa účet zobrazil. Účet sa uložil; otvorte zoznam znova.',
    'deleteReason': 'Vymazať tento účet',
    'addTitle': 'Pridať účet',
    'checkTitle': 'Skontrolujte účet',
    'notAdded': 'Účet sa nepridal',
    'qrNotAccount': 'Tento QR kód nie je dvojfaktorový účet. {why}',
    'saveAuthFailed':
        'Overenie odtlačkom prsta alebo tvárou neprešlo. Nič sa neuložilo.',
    'saveBiometricsChanged':
        'Vaše odtlačky prstov alebo tvár sa zmenili. Nič sa neuložilo.',
    'saveFailed': 'Účet sa nepodarilo uložiť. Skúste to znova.',
    'pickWay': 'Vyberte spôsob, akým vám služba účet poskytuje.',
    'scanText':
        'O kameru sa aplikácia žiada až teraz. Žiadny obrázok sa neukladá.',
    'linkText': 'Odkaz, ktorý začína na otpauth://',
    'keyText': 'Písmená a číslice, ktoré služba zobrazuje vedľa QR kódu.',
    'linkLabel': 'Odkaz otpauth://',
    'linkHelp': 'Vložte celý odkaz.',
    'continue': 'Pokračovať',
    'serviceHelp': 'Napríklad GitHub.',
    'accountNameHelp': 'Zvyčajne vaša e-mailová adresa.',
    'setupKey': 'Kľúč na nastavenie',
    'setupKeyHelp': 'Na medzerách a veľkých písmenách nezáleží.',
    'moreSettings': 'Ďalšie nastavenia',
    'moreSettingsHelp': 'Meňte ich, len ak to služba vyžaduje.',
    'algorithm': 'Algoritmus',
    'digits': 'Počet číslic',
    'period': 'Platnosť kódu v sekundách',
    'noServiceName': 'Bez názvu služby',
    'counterBased': 'Podľa počítadla, začína na {counter}',
    'timeBased': 'Časový, nový kód každých {period} sekúnd',
    'digitsLine': '{digits} číslic, {algorithm}',
    'keyStrength': 'Sila kľúča: {bits} bitov',
    'checkText':
        'Skontrolujte, či je to účet, ktorý ste mali na mysli. Po uložení sa kľúč už nikdy nezobrazí.',
    'saving': 'Ukladá sa',
    'scanHelp':
        'Namierte kameru na QR kód, ktorý zobrazuje služba. Nič sa nenahráva.',
    'cameraUnavailable': 'Kamera nie je dostupná',
    'cameraDenied':
        'Povoľte tejto aplikácii kameru v nastaveniach telefónu, alebo namiesto toho vložte odkaz či zadajte kľúč na nastavenie.',
    'cameraOther':
        'Namiesto toho môžete vložiť odkaz alebo zadať kľúč na nastavenie.',
    'backupTitle': 'Záloha',
    'backupIntro':
        'Záloha je súbor so všetkými vašimi dvojfaktorovými účtami, zamknutý prístupovou frázou, ktorú si zvolíte. Uložte ho na bezpečné miesto. '
            'Nič sa neposiela na žiadny server a bez prístupovej frázy je súbor nepoužiteľný: stratená prístupová fráza sa nedá obnoviť.',
    'createBackup': 'Vytvoriť zálohu',
    'createBackupText':
        'Vyžiada si odtlačok prsta alebo tvár, potom prístupovú frázu.',
    'restoreTitle': 'Obnoviť zo zálohy',
    'restoreText':
        'Pridá účty zo záložného súboru. Účty, ktoré už máte, sa preskočia.',
    'strengthRefused': 'Príliš slabá',
    'strengthFair': 'Priemerná',
    'strengthGood': 'Dobrá',
    'strengthStrong': 'Silná',
    'strength': 'Sila: {word}',
    'passphrase': 'Prístupová fráza',
    'passphraseHelp':
        'Najmenej 12 znakov. Najlepšia je fráza z niekoľkých slov.',
    'hidePassphrase': 'Skryť prístupovú frázu',
    'showPassphrase': 'Zobraziť prístupovú frázu',
    'typeAgain': 'Zadajte ju znova',
    'passphraseMismatch': 'Zadané frázy sa nezhodujú.',
    'backupFailed':
        'Zálohu sa nepodarilo vytvoriť. Nič sa neuložilo. Skúste to znova.',
    'noRecoveryTitle': 'Prístupová fráza sa nedá obnoviť',
    'noRecovery':
        'Nikde sa neukladá. Ak ju stratíte, zálohu nebude môcť otvoriť nikto, ani my.',
    'noBackupMade': 'Záloha sa nevytvorila',
    'backupReady': 'Záloha je pripravená',
    'backupDoneOne':
        'V súbore je {n} účet. Uložte ho tam, kam sa dostanete len vy, a prístupovú frázu uchovávajte oddelene od neho.',
    'backupDoneFew':
        'V súbore sú {n} účty. Uložte ho tam, kam sa dostanete len vy, a prístupovú frázu uchovávajte oddelene od neho.',
    'backupDoneMany':
        'V súbore je {n} účtov. Uložte ho tam, kam sa dostanete len vy, a prístupovú frázu uchovávajte oddelene od neho.',
    'protecting':
        'Záloha sa zabezpečuje. Zámerne to trvá niekoľko sekúnd: práve vďaka tomu sa prístupová fráza ťažko uhádne.',
    'working': 'Pracuje sa',
    'createTheBackup': 'Vytvoriť zálohu',
    'readFailed': 'Súbor sa nepodarilo prečítať. Nič sa nepridalo.',
    'addAllFailed':
        'Nepodarilo sa pridať všetky účty. Otvorte zoznam, pozrite sa, ktoré tam sú, a skúste to znova.',
    'afterBiometrics':
        'Účty, ktoré boli v tomto telefóne, sa už nedajú otvoriť. Obnovenie zálohy ich nahradí účtami zo súboru.',
    'nothingAdded': 'Nič sa nepridalo',
    'restored': 'Obnovené',
    'restoredOne': 'Pridal sa {n} účet.',
    'restoredFew': 'Pridali sa {n} účty.',
    'restoredMany': 'Pridalo sa {n} účtov.',
    'chooseFile': 'Vybrať záložný súbor',
    'nothingNew': 'V tejto zálohe nie je nič nové',
    'willAddOne': 'Pridá sa tento účet',
    'willAddFew': 'Pridajú sa tieto {n} účty',
    'willAddMany': 'Pridá sa týchto {n} účtov',
    'duplicatesOne': '{n} účet tu už je a preskočí sa.',
    'duplicatesFew': '{n} účty tu už sú a preskočia sa.',
    'duplicatesMany': '{n} účtov tu už je a preskočí sa.',
    'restoreThese': 'Obnoviť tieto účty',
    'addThese': 'Pridať tieto účty',
  });
}
