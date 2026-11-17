import 'dart:ui';

import '../l10n/texts.dart';

/// The texts of the settings frame, its sheets, and the messages of the settings controller and client that a settings screen shows (T-251). English and
/// Slovak. A controller or client has no `BuildContext`, so it takes the phone's language ([SecurityTexts.of] with no locale), the same the screens show.
class SecurityTexts extends Texts {
  const SecurityTexts._(super.values);

  /// The texts for [locale] (the phone's language when null).
  factory SecurityTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  static const en = SecurityTexts._({
    // The frame of the settings screens.
    'notAvailableYet': 'Not available yet',
    'notAvailableYetBody':
        'This Biatec OIDC does not offer device and settings management; it only runs against the mock for now.',
    'notAvailable': 'Not available',
    'notAvailableBody': 'Not available.',
    'tryAgain': 'Try again',
    'problem': 'Problem',
    'done': 'Done',
    // The sheets.
    'keepAsItIs': 'Keep as it is',
    'keep': 'Keep',
    'amount': 'Amount',
    'eur': 'EUR',
    'save': 'Save',
    'cancel': 'Cancel',
    'amountProblem': 'Enter an amount like 250 or 250.50.',
    'raises':
        'This raises it: your phone will ask for your fingerprint or face first.',
    'lowers': 'This lowers it: it applies at once.',
    'same': 'This is the amount it already has.',
    'lowerRaiseHint':
        'Lowering applies at once. Raising needs approval on this device.',
    'readFirst': 'Read this first',
    'phoneAsks': 'Your phone will ask for you',
    'phoneAsksBody':
        'After you confirm, it asks for your fingerprint or face before this applies.',
    // The settings controller.
    'notEnrolled': 'This device is not enrolled.',
    'cancelledHere':
        'This enrollment was cancelled. Enroll this device again to use it.',
    'enrollmentCancelled': 'Enrollment cancelled.',
    'cannotProtect': 'This device cannot protect approvals.',
    'activationCancelled': 'Cancelled. Nothing was activated.',
    'couldNotApprove': 'Could not approve: {reason}',
    'approveChange': 'Approve this settings change',
    'approveChangeSubtitle': 'Lowers a protection on your Biatec account',
    'changeSetting': 'Change a security setting',
    'biometricFailed': 'Biometric check did not pass.',
    'approvedApplied': 'Approved on this device and applied.',
    'applied': 'Applied.',
    'notActive':
        'This device is not active yet, so it cannot approve a change that lowers your protection. Changes that raise it still work.',
    'approvalCancelled': 'Approval cancelled. Nothing was changed.',
    // The settings client.
    'proofFailed': 'The device proof could not be made: {reason}',
    'pinning':
        'The connection to {host} could not be verified, so nothing was sent. If this keeps happening, update the app.',
    'unreachable': 'Biatec OIDC is not reachable: {reason}',
    'unexpected': 'Unexpected response ({status}).',
    'loadDevicesFailed': 'Could not load devices ({status}): {detail}',
    'malformedDevices': 'Malformed device list.',
    'onlyPending': 'Only a pending enrollment can be cancelled.',
    'cancelFailed': 'Could not cancel ({status}): {detail}',
    'alreadyActive':
        'That device is already active, or cannot be approved by a device.',
    'noLongerWaiting': 'That device is no longer waiting for activation.',
    'activateFailed': 'Could not activate it ({status}): {detail}',
    'loadSettingsFailed': 'Could not load settings ({status}): {detail}',
    'malformedSettings': 'Malformed settings.',
    'refused': 'The change was refused ({status}): {detail}',
  });

  static const sk = SecurityTexts._({
    'notAvailableYet': 'Zatiaľ nedostupné',
    'notAvailableYetBody':
        'Tento Biatec OIDC zatiaľ neponúka správu zariadení a nastavení; zatiaľ to funguje iba s testovacím serverom (mock).',
    'notAvailable': 'Nedostupné',
    'notAvailableBody': 'Nedostupné.',
    'tryAgain': 'Skúsiť znova',
    'problem': 'Problém',
    'done': 'Hotovo',
    'keepAsItIs': 'Ponechať, ako je',
    'keep': 'Ponechať',
    'amount': 'Suma',
    'eur': 'EUR',
    'save': 'Uložiť',
    'cancel': 'Zrušiť',
    'amountProblem': 'Zadajte sumu ako 250 alebo 250.50.',
    'raises':
        'Týmto sa zvyšuje: telefón si najprv vypýta odtlačok prsta alebo tvár.',
    'lowers': 'Týmto sa znižuje: prejaví sa hneď.',
    'same': 'Túto sumu už má.',
    'lowerRaiseHint':
        'Zníženie platí hneď. Zvýšenie treba schváliť v tomto zariadení.',
    'readFirst': 'Najprv si prečítajte',
    'phoneAsks': 'Telefón vás overí',
    'phoneAsksBody':
        'Po potvrdení si pred uplatnením vypýta odtlačok prsta alebo tvár.',
    'notEnrolled': 'Toto zariadenie nie je zaregistrované.',
    'cancelledHere':
        'Táto registrácia bola zrušená. Ak chcete toto zariadenie používať, zaregistrujte ho znova.',
    'enrollmentCancelled': 'Registrácia bola zrušená.',
    'cannotProtect': 'Toto zariadenie nedokáže chrániť schválenia.',
    'activationCancelled': 'Zrušené. Nič sa neaktivovalo.',
    'couldNotApprove': 'Nepodarilo sa schváliť: {reason}',
    'approveChange': 'Schváliť túto zmenu nastavení',
    'approveChangeSubtitle': 'Znižuje ochranu vášho účtu Biatec',
    'changeSetting': 'Zmeniť nastavenie zabezpečenia',
    'biometricFailed': 'Biometrické overenie neprešlo.',
    'approvedApplied': 'Schválené v tomto zariadení a uplatnené.',
    'applied': 'Uplatnené.',
    'notActive':
        'Toto zariadenie ešte nie je aktívne, preto nemôže schváliť zmenu, ktorá znižuje vašu ochranu. Zmeny, ktoré ju zvyšujú, fungujú aj naďalej.',
    'approvalCancelled': 'Schválenie bolo zrušené. Nič sa nezmenilo.',
    'proofFailed': 'Dôkaz zariadenia sa nepodarilo vytvoriť: {reason}',
    'pinning':
        'Spojenie s {host} sa nepodarilo overiť, preto sa nič neodoslalo. Ak sa to opakuje, aktualizujte aplikáciu.',
    'unreachable': 'Biatec OIDC nie je dostupný: {reason}',
    'unexpected': 'Neočakávaná odpoveď ({status}).',
    'loadDevicesFailed': 'Zariadenia sa nepodarilo načítať ({status}): {detail}',
    'malformedDevices': 'Zoznam zariadení má chybný formát.',
    'onlyPending': 'Zrušiť sa dá iba registrácia, ktorá ešte čaká.',
    'cancelFailed': 'Nepodarilo sa zrušiť ({status}): {detail}',
    'alreadyActive':
        'Toto zariadenie je už aktívne alebo ho iné zariadenie nemôže schváliť.',
    'noLongerWaiting': 'Toto zariadenie už nečaká na aktiváciu.',
    'activateFailed': 'Nepodarilo sa ho aktivovať ({status}): {detail}',
    'loadSettingsFailed': 'Nastavenia sa nepodarilo načítať ({status}): {detail}',
    'malformedSettings': 'Nastavenia majú chybný formát.',
    'refused': 'Zmena bola odmietnutá ({status}): {detail}',
  });
}
