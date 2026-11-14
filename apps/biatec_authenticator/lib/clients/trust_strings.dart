import 'dart:ui';

/// The texts of the user-held trust settings: how a service is shown, the "New service" banner and "don't ask again" on the approval card, the approval threshold and the linked services. English and Slovak,
/// chosen by the phone's language (Slovak for `sk`, English otherwise). A text may hold `{name}` places that [t] fills; what goes into one is the user's own value or a value this app computed
/// (an application id, a date), never a name from Biatec OIDC or a service.
class TrustStrings {
  const TrustStrings._(this._t);

  factory TrustStrings.of([Locale? locale]) {
    final language =
        (locale ?? PlatformDispatcher.instance.locale).languageCode;
    return language == 'sk' ? sk : en;
  }

  final Map<String, String> _t;

  /// The text for [key] with each `{name}` of it replaced from [args].
  String t(String key, [Map<String, String> args = const {}]) {
    // One pass over the template: a value that itself holds `{name}` is never substituted again.
    return _t[key]!.replaceAllMapped(
        RegExp(r'\{([A-Za-z]+)\}'), (m) => args[m[1]] ?? m[0]!);
  }

  /// Every key, so a test can check that both languages have all of them.
  Iterable<String> get keys => _t.keys;

  static const en = TrustStrings._({
    // The identity widget.
    'warning': 'Warning',
    'unprovenApplication': 'Unproven application',
    'unprovenExplain':
        'Biatec cannot confirm who this is. Approve only if you started this action yourself.',
    'provenDomain': 'Proven domain',
    'application': 'application',
    'youCallIt': 'You call it:',
    // The approval card.
    'newServiceTitle': 'New service',
    'newServiceBody':
        'Approving links this service to your authenticator. It can then send you requests. You can unlink it in Settings.',
    'dontAskAgain':
        "Don't ask again for application {id} from {who} while the amount is under my threshold",
    // Settings.
    'eur': 'EUR',
    'amount': 'Amount',
    'save': 'Save',
    'cancel': 'Cancel',
    'thresholdTitle': 'Approval threshold',
    'thresholdTileSubtitle':
        '{amount}. Requests above it ask you on this phone.',
    'currentThreshold': 'Now',
    'thresholdIntro':
        'Requests above this amount ask you on this phone. Requests under it are signed without asking, unless the service is new, set to always ask, or the value is unknown.',
    'thresholdHelp': 'Choose an amount or type your own, for example 250.50.',
    'alwaysAsk': 'Always ask',
    'otherAmount': 'Other amount',
    'amountProblem': 'Enter an amount like 250 or 250.50.',
    'raiseExplain':
        'Requests under this amount will be signed without asking you.',
    'raiseNeedsApproval':
        'Raising it is a change you approve on this phone with your fingerprint or face.',
    'lowerExplain': 'This lowers it. It applies at once.',
    'sameAmount': 'This is the amount it already has.',
    'askTestNetworks': 'Ask on test networks',
    'askTestNetworksHelp':
        'Also ask for requests on test networks that are above the threshold or of unknown value. Turning it off is approved on this phone.',
    'blockUnproven': 'Block services without a proven domain',
    'blockUnprovenHelp':
        'A service that has not proven its domain cannot ask you for anything. Turning it off is approved on this phone.',
    'inactiveTitle': 'This device is not active yet',
    'inactiveBody':
        'You can read your settings here, but not change them, until this device is active.',
    'inactiveUntil': 'It becomes active on {when}.',
    // Linked services.
    'linkedServicesTitle': 'Linked services',
    'linkedServicesTile': 'Linked services',
    'linkedServicesSubtitle':
        'The services that may send you requests, and what each may do without asking.',
    'noLinkedServices':
        'No service is linked yet. A service is linked when you approve its first request.',
    'linkedOn': 'Linked {when}',
    'notLinkedDate': 'Link date not known',
    'pausedShort': 'Paused',
    'alwaysAskShort': 'Always asks',
    'serviceTitle': 'Linked service',
    'serviceGone': 'This service is not linked any more.',
    'yourLabel': 'Your label',
    'noLabel': 'None. Add a name that helps you recognise it.',
    'editLabel': 'Edit label',
    'labelField': 'Label',
    'labelHelp':
        'Your own name for this service, up to {max} characters. It is shown next to the service, never instead of it.',
    'labelEmpty': 'Type a label.',
    'labelTooLong': 'At most {max} characters.',
    'labelPadded': 'No space at the start or the end.',
    'labelNotAllowed':
        'Letters, digits, punctuation and emoji only: no line breaks, invisible, right-to-left or combining characters.',
    'alwaysAskService': 'Always ask for this service',
    'alwaysAskServiceHelp':
        'Every request from it asks you on this phone, whatever the amount. Turning it off is approved on this phone.',
    'serviceThresholdTitle': 'Threshold for this service',
    'usesMyThreshold': 'Uses your threshold ({amount}).',
    'changeThreshold': 'Change threshold',
    'useMyThreshold': 'Use my threshold',
    'trustedApplications': 'Applications it may call without asking',
    'noTrustedApplications':
        'None. You can trust an application when you approve a request that calls it.',
    'applicationOn': 'Application {id} on {network}',
    'removeApplication': 'Stop trusting this application',
    'pauses': 'Pauses',
    'notPaused': 'Not paused.',
    'pausedUntil': 'Paused until {when}',
    'pausedOrigin': '{origin} is paused until {when}',
    'pausedBody':
        'It cannot ask for approvals while paused. Lifting the pause is approved on this phone.',
    'liftPause': 'Lift pause',
    'liftPauseTitle': 'Lift the pause for {origin}?',
    'liftPauseBody': 'It will be able to ask for approvals again straight away.',
    'keepPaused': 'Keep paused',
    'unlink': 'Unlink this service',
    'unlinkHelp':
        'Its next request is a new service again, and the applications you trusted for it are forgotten.',
    'unlinkTitle': 'Unlink this service?',
    'unlinkBody':
        'It can no longer send you requests without you approving it as a new service again. Applications you trusted for it and its settings are removed.',
    'pausesTitle': 'Paused services',
    'thresholdChangedTitle': 'The threshold changed',
    'thresholdChangedBody':
        'It was changed while you were typing, so the field shows the new amount. Check it before you save.',
    'askTestNetworksOffTitle': 'Stop asking on test networks?',
    'askTestNetworksOffBody':
        'Requests on test networks will no longer ask because of their amount or an unknown value. Every other reason to ask still applies.',
    'blockUnprovenOffTitle': 'Let services without a proven domain ask?',
    'blockUnprovenOffBody':
        'A service that has not proven its domain will be able to send you requests again.',
    'turnOff': 'Turn off',
    'keepOn': 'Keep it on',
    'moreApplications': 'More applications could be trusted than the {max} offered here. Only the first {max} can be remembered with one approval.',
    'keepLinked': 'Keep it linked',
  });

  static const sk = TrustStrings._({
    'warning': 'Varovanie',
    'unprovenApplication': 'Neoverená aplikácia',
    'unprovenExplain':
        'Biatec nevie potvrdiť, kto to je. Schváľte iba vtedy, ak ste túto akciu spustili sami.',
    'provenDomain': 'Overená doména',
    'application': 'aplikácia',
    'youCallIt': 'Nazývate ju:',
    'newServiceTitle': 'Nová služba',
    'newServiceBody':
        'Schválením sa táto služba prepojí s vaším autentifikátorom. Potom vám môže posielať žiadosti. Prepojenie môžete zrušiť v Nastaveniach.',
    'dontAskAgain':
        'Nepýtať sa znova na aplikáciu {id} od {who}, kým je suma pod mojím limitom',
    'eur': 'EUR',
    'amount': 'Suma',
    'save': 'Uložiť',
    'cancel': 'Zrušiť',
    'thresholdTitle': 'Limit na schvaľovanie',
    'thresholdTileSubtitle':
        '{amount}. Žiadosti nad ním sa pýtajú v tomto telefóne.',
    'currentThreshold': 'Teraz',
    'thresholdIntro':
        'Žiadosti nad touto sumou sa pýtajú v tomto telefóne. Žiadosti pod ňou sa podpíšu bez pýtania, pokiaľ služba nie je nová, nemá nastavené „Vždy sa pýtať“ alebo nie je hodnota známa.',
    'thresholdHelp': 'Vyberte sumu alebo zadajte vlastnú, napríklad 250.50.',
    'alwaysAsk': 'Vždy sa pýtať',
    'otherAmount': 'Iná suma',
    'amountProblem': 'Zadajte sumu ako 250 alebo 250.50.',
    'raiseExplain': 'Žiadosti pod touto sumou sa podpíšu bez pýtania.',
    'raiseNeedsApproval':
        'Zvýšenie schválite v tomto telefóne odtlačkom prsta alebo tvárou.',
    'lowerExplain': 'Týmto sa limit znižuje. Prejaví sa hneď.',
    'sameAmount': 'Túto sumu už limit má.',
    'askTestNetworks': 'Pýtať sa aj v testovacích sieťach',
    'askTestNetworksHelp':
        'Pýtať sa aj na žiadosti v testovacích sieťach nad limitom alebo s neznámou hodnotou. Vypnutie schvaľujete v tomto telefóne.',
    'blockUnproven': 'Blokovať služby bez overenej domény',
    'blockUnprovenHelp':
        'Služba, ktorá si neoverila doménu, vás o nič nemôže požiadať. Vypnutie schvaľujete v tomto telefóne.',
    'inactiveTitle': 'Toto zariadenie ešte nie je aktívne',
    'inactiveBody':
        'Nastavenia tu môžete čítať, ale nie meniť, kým toto zariadenie nebude aktívne.',
    'inactiveUntil': 'Aktívne bude {when}.',
    'linkedServicesTitle': 'Prepojené služby',
    'linkedServicesTile': 'Prepojené služby',
    'linkedServicesSubtitle':
        'Služby, ktoré vám môžu posielať žiadosti, a čo smie každá bez pýtania.',
    'noLinkedServices':
        'Zatiaľ nie je prepojená žiadna služba. Služba sa prepojí, keď schválite jej prvú žiadosť.',
    'linkedOn': 'Prepojená {when}',
    'notLinkedDate': 'Dátum prepojenia nie je známy',
    'pausedShort': 'Pozastavená',
    'alwaysAskShort': 'Vždy sa pýta',
    'serviceTitle': 'Prepojená služba',
    'serviceGone': 'Táto služba už nie je prepojená.',
    'yourLabel': 'Váš názov',
    'noLabel': 'Žiadny. Pridajte názov, podľa ktorého ju spoznáte.',
    'editLabel': 'Upraviť názov',
    'labelField': 'Názov',
    'labelHelp':
        'Váš vlastný názov tejto služby, najviac {max} znakov. Zobrazuje sa vedľa služby, nikdy namiesto nej.',
    'labelEmpty': 'Zadajte názov.',
    'labelTooLong': 'Najviac {max} znakov.',
    'labelPadded': 'Bez medzery na začiatku a na konci.',
    'labelNotAllowed':
        'Iba písmená, číslice, interpunkcia a emoji: žiadne zalomenia riadkov, neviditeľné znaky, znaky sprava doľava ani kombinačné znaky.',
    'alwaysAskService': 'Vždy sa pýtať pri tejto službe',
    'alwaysAskServiceHelp':
        'Každá jej žiadosť sa pýta v tomto telefóne bez ohľadu na sumu. Vypnutie schvaľujete v tomto telefóne.',
    'serviceThresholdTitle': 'Limit pre túto službu',
    'usesMyThreshold': 'Používa váš limit ({amount}).',
    'changeThreshold': 'Zmeniť limit',
    'useMyThreshold': 'Použiť môj limit',
    'trustedApplications': 'Aplikácie, ktoré smie volať bez pýtania',
    'noTrustedApplications':
        'Žiadne. Aplikácii môžete dôverovať pri schvaľovaní žiadosti, ktorá ju volá.',
    'applicationOn': 'Aplikácia {id} v sieti {network}',
    'removeApplication': 'Prestať dôverovať tejto aplikácii',
    'pauses': 'Pozastavenia',
    'notPaused': 'Nie je pozastavená.',
    'pausedUntil': 'Pozastavená do {when}',
    'pausedOrigin': '{origin} je pozastavený do {when}',
    'pausedBody':
        'Počas pozastavenia nemôže žiadať o schválenie. Zrušenie pozastavenia schvaľujete v tomto telefóne.',
    'liftPause': 'Zrušiť pozastavenie',
    'liftPauseTitle': 'Zrušiť pozastavenie pre {origin}?',
    'liftPauseBody': 'Bude môcť hneď znova žiadať o schválenie.',
    'keepPaused': 'Ponechať pozastavené',
    'unlink': 'Zrušiť prepojenie tejto služby',
    'unlinkHelp':
        'Jej ďalšia žiadosť bude opäť nová služba a aplikácie, ktorým ste pre ňu dôverovali, sa zabudnú.',
    'unlinkTitle': 'Zrušiť prepojenie tejto služby?',
    'unlinkBody':
        'Nebude vám môcť posielať žiadosti, kým ju znova neschválite ako novú službu. Aplikácie, ktorým ste pre ňu dôverovali, aj jej nastavenia sa odstránia.',
    'pausesTitle': 'Pozastavené služby',
    'thresholdChangedTitle': 'Limit sa zmenil',
    'thresholdChangedBody':
        'Zmenil sa počas písania, preto pole ukazuje novú sumu. Pred uložením ju skontrolujte.',
    'askTestNetworksOffTitle': 'Prestať sa pýtať v testovacích sieťach?',
    'askTestNetworksOffBody':
        'Žiadosti v testovacích sieťach sa už nebudú pýtať kvôli sume ani neznámej hodnote. Ostatné dôvody na pýtanie platia ďalej.',
    'blockUnprovenOffTitle': 'Povoliť služby bez overenej domény?',
    'blockUnprovenOffBody':
        'Služba, ktorá si neoverila doménu, vám bude môcť znova posielať žiadosti.',
    'turnOff': 'Vypnúť',
    'keepOn': 'Nechať zapnuté',
    'moreApplications': 'Dôverovať by sa dalo viac aplikácií, než je tu {max} ponúkaných. Jedným schválením sa dá zapamätať najviac prvých {max}.',
    'keepLinked': 'Ponechať prepojenú',
  });
}
