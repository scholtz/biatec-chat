import 'package:material_ui/material_ui.dart';

import '../../l10n/texts.dart';

/// The texts of the approval history (T-251): the list, its filters and day headings, the status of a decision and the receipt. English and Slovak; see
/// [Texts].
class HistoryTexts extends Texts {
  const HistoryTexts._(super.values);

  factory HistoryTexts.of([Locale? locale]) =>
      appLanguageIsSlovak(locale) ? sk : en;

  /// The language of the widget tree at [context] (the device's language when there is none).
  factory HistoryTexts.ofContext(BuildContext context) =>
      HistoryTexts.of(Localizations.maybeLocaleOf(context));

  static const en = HistoryTexts._({
    'title': 'Approval history',
    'readFailedTitle': 'History could not be read',
    'readFailedBody': 'The decisions stored on this device did not open.',
    'refreshFailedTitle': 'History could not be refreshed',
    'refreshFailedBody': 'Showing what was read before.',
    'tryAgain': 'Try again',
    'emptyTitle': 'No approvals yet',
    'emptyBody':
        'Requests you approve or deny on this device are listed here with their receipts.',
    'noMatch': 'No decision on this device matches this filter.',
    'showAll': 'Show all',
    // The filters and the statuses.
    'filterAll': 'All',
    'filterAllEmpty': 'Nothing here yet',
    'filterApprovedEmpty': 'Nothing approved yet',
    'filterDeniedEmpty': 'Nothing denied yet',
    'filterRefusedEmpty': 'Nothing refused yet',
    'statusApproved': 'Approved',
    'statusDenied': 'Denied',
    'statusRefused': 'Refused',
    'statusRefusedLong': 'Refused after three wrong codes',
    // The days.
    'today': 'Today',
    'yesterday': 'Yesterday',
    // A row, for a screen reader.
    'spokenFrom': 'From {client}, {when}',
    'spokenAt': 'At {when}',
    'doubleTapReceipt': 'Double tap to open the receipt.',
    // The receipt.
    'receiptTitle': 'Receipt',
    'idCopied': 'Receipt id copied',
    'refusedTitle': 'The request was refused',
    'refusedBody': 'Three wrong codes ended it. Nothing was approved.',
    'decision': 'Decision',
    'client': 'Client',
    'decided': 'Decided',
    'receiptId': 'Receipt id',
    'copyId': 'Copy the receipt id',
    'deviceKeyId': 'Device key id',
    'signature': 'Signature',
    'copyTitle': 'A copy for reading',
    'copyBody':
        'The one-time code you typed is not kept, so this copy no longer verifies against its signature. Biatec OIDC holds the receipt that does.',
    'storedReceipt': 'Stored receipt',
    'asKept': 'As this device kept it',
  });

  static const sk = HistoryTexts._({
    'title': 'História schválení',
    'readFailedTitle': 'Históriu sa nepodarilo prečítať',
    'readFailedBody': 'Rozhodnutia uložené v tomto zariadení sa nepodarilo otvoriť.',
    'refreshFailedTitle': 'Históriu sa nepodarilo obnoviť',
    'refreshFailedBody': 'Zobrazuje sa to, čo sa prečítalo predtým.',
    'tryAgain': 'Skúsiť znova',
    'emptyTitle': 'Zatiaľ žiadne schválenia',
    'emptyBody':
        'Žiadosti, ktoré v tomto zariadení schválite alebo zamietnete, sa tu zobrazia aj s potvrdeniami.',
    'noMatch': 'Tomuto filtru nezodpovedá žiadne rozhodnutie v tomto zariadení.',
    'showAll': 'Zobraziť všetko',
    'filterAll': 'Všetko',
    'filterAllEmpty': 'Zatiaľ tu nič nie je',
    'filterApprovedEmpty': 'Zatiaľ nič neschválené',
    'filterDeniedEmpty': 'Zatiaľ nič nezamietnuté',
    'filterRefusedEmpty': 'Zatiaľ nič neodmietnuté',
    'statusApproved': 'Schválené',
    'statusDenied': 'Zamietnuté',
    'statusRefused': 'Odmietnuté',
    'statusRefusedLong': 'Odmietnuté po troch nesprávnych kódoch',
    'today': 'Dnes',
    'yesterday': 'Včera',
    'spokenFrom': 'Od {client}, {when}',
    'spokenAt': 'Dňa {when}',
    'doubleTapReceipt': 'Dvojitým ťuknutím otvoríte potvrdenie.',
    'receiptTitle': 'Potvrdenie',
    'idCopied': 'ID potvrdenia sa skopírovalo',
    'refusedTitle': 'Žiadosť bola odmietnutá',
    'refusedBody': 'Ukončili ju tri nesprávne kódy. Nič sa neschválilo.',
    'decision': 'Rozhodnutie',
    'client': 'Klient',
    'decided': 'Rozhodnuté',
    'receiptId': 'ID potvrdenia',
    'copyId': 'Skopírovať ID potvrdenia',
    'deviceKeyId': 'ID kľúča zariadenia',
    'signature': 'Podpis',
    'copyTitle': 'Kópia na čítanie',
    'copyBody':
        'Jednorazový kód, ktorý ste zadali, sa neuchováva, preto sa táto kópia už nedá overiť voči svojmu podpisu. Potvrdenie, ktoré sa overiť dá, má Biatec OIDC.',
    'storedReceipt': 'Uložené potvrdenie',
    'asKept': 'Ako ho uchovalo toto zariadenie',
  });
}
