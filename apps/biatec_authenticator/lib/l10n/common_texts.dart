import 'dart:ui';

import 'texts.dart';

/// Texts that many screens share, the navigation, and the screens around the app (T-251).
class CommonTexts extends Texts {
  const CommonTexts._(super.values);

  factory CommonTexts.of([Locale? locale]) => appLanguageIsSlovak(locale) ? sk : en;

  static const en = CommonTexts._({
    'tryAgain': 'Try again',
    'offline': 'Offline',
    'error': 'Error',
    'offlineTitle': 'You are offline',
    'somethingWrong': 'Something went wrong',
    'offlineMessage': 'Showing the last known state. Requests can only be approved with a live connection.',
    'loading': 'Loading',
    'note': 'Note',
    'success': 'Success',
    'warning': 'Warning',
    'problem': 'Problem',
    'pendingCount': '{n} pending',
    'tab.codes': 'Codes',
    'tab.approvals': 'Approvals',
    'tab.history': 'History',
    'tab.devices': 'Devices',
    'tab.settings': 'Settings',
    'stuckTitle': 'Still checking this device',
    'stuckMessage': 'The secure storage on this device is slow to answer. Your enrollment is not lost.',
    'unreadableTitle': 'Could not read this device',
    'unreadableMessage':
        'The secure storage on this device did not open. Your enrollment is not lost. Try again, and restart the app if it keeps failing.',
    'recoveryTitle': 'Save your recovery code',
    'recoveryMessage': 'It is shown once and lets you replace this device if it is lost.',
    'showCode': 'Show code',
    'newRequest': 'New approval request',
    'open': 'Open',
    'cannotStart': 'This build cannot start',
  });

  static const sk = CommonTexts._({
    'tryAgain': 'Skúsiť znova',
    'offline': 'Offline',
    'error': 'Chyba',
    'offlineTitle': 'Ste offline',
    'somethingWrong': 'Niečo sa pokazilo',
    'offlineMessage': 'Zobrazuje sa posledný známy stav. Žiadosti sa dajú schváliť iba s pripojením.',
    'loading': 'Načítava sa',
    'note': 'Poznámka',
    'success': 'Hotovo',
    'warning': 'Upozornenie',
    'problem': 'Problém',
    'pendingCount': 'Čakajúce: {n}',
    'tab.codes': 'Kódy',
    'tab.approvals': 'Schválenia',
    'tab.history': 'História',
    'tab.devices': 'Zariadenia',
    'tab.settings': 'Nastavenia',
    'stuckTitle': 'Zariadenie sa stále kontroluje',
    'stuckMessage': 'Zabezpečené úložisko tohto zariadenia odpovedá pomaly. Vaša registrácia sa nestratila.',
    'unreadableTitle': 'Zariadenie sa nepodarilo prečítať',
    'unreadableMessage':
        'Zabezpečené úložisko tohto zariadenia sa neotvorilo. Vaša registrácia sa nestratila. Skúste to znova, a ak to stále nejde, reštartujte aplikáciu.',
    'recoveryTitle': 'Uložte si obnovovací kód',
    'recoveryMessage': 'Zobrazí sa iba raz a umožní vám nahradiť toto zariadenie, ak ho stratíte.',
    'showCode': 'Zobraziť kód',
    'newRequest': 'Nová žiadosť o schválenie',
    'open': 'Otvoriť',
    'cannotStart': 'Táto verzia sa nemôže spustiť',
  });
}
