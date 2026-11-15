import 'dart:ui';

/// The texts of the relying-party features (protocol 1.2): linking a service by QR code and the payment, login and action cards. English and Slovak, chosen by the phone's language (Slovak for `sk`,
/// English otherwise). A text may hold `{name}` places that [t] fills; what goes into one is a value this app computed or text a service wrote that already passed the text rules, and it is always
/// drawn as plain text.
class RpStrings {
  const RpStrings._(this._t);

  factory RpStrings.of([Locale? locale]) {
    final language =
        (locale ?? PlatformDispatcher.instance.locale).languageCode;
    return language == 'sk' ? sk : en;
  }

  final Map<String, String> _t;

  static final RegExp _place = RegExp(r'\{([A-Za-z]+)\}');

  /// The text for [key] with each `{name}` of it replaced from [args], in one pass (a value that itself holds `{name}` is never substituted again).
  String t(String key, [Map<String, String> args = const {}]) {
    return _t[key]!.replaceAllMapped(_place, (m) => args[m[1]] ?? m[0]!);
  }

  /// Every key, so a test can check that both languages have all of them.
  Iterable<String> get keys => _t.keys;

  static const en = RpStrings._({
    // The cards.
    'cannotShow': 'This request cannot be shown; update the app',
    'cannotShowBody': 'It cannot be approved here. Nothing was approved.',
    'writtenByService': 'Written by the service',
    'reportedByService': 'Reported by the service',
    'originLabel': 'Origin, as reported by the service',
    'paymentHeading': 'Confirm a payment',
    'payee': 'Payee',
    'account': 'Account',
    'accountNumber': 'Account number',
    'accountScheme': 'Account type',
    'remittance': 'Reference',
    'executionDate': 'Execution date',
    'signInTo': 'Sign in to {who}',
    'loginNothing': 'The service reported nothing about this sign-in.',
    'ctxIp': 'IP address',
    'ctxLocation': 'Location',
    'ctxUserAgent': 'Browser or app',
    // Link a service.
    'linkTitle': 'Link a service',
    'linkAServiceHelp':
        'Scan the QR code the service shows you. The service has to be one you started linking yourself.',
    'pasteCode': 'Paste a link code',
    'pasteField': 'Link code',
    'pasteOpen': 'Open',
    'cancel': 'Cancel',
    'notALinkCode': 'This is not a code for linking a service.',
    'differentService': 'This code is for a different authenticator service',
    'codeProblemTitle': 'This code cannot be used',
    'linkWhat':
        'This service will be able to send you approval requests. It cannot move anything without your approval.',
    'serviceCallsYou': 'The service calls you: {label}',
    'linkButton': 'Link',
    'dontLink': 'Don\'t link',
    'unprovenTitle': 'This service has not proven who it is',
    'unprovenBody':
        'Biatec cannot confirm who this is. Link it only if you started this yourself and you know this service.',
    'linkAnyway': 'Link anyway',
    'linkedTitle': 'Linked.',
    'linkedBody':
        'This service can now send you approval requests. You can unlink it in Settings, Linked services.',
    'deniedTitle': 'Not linked.',
    'deniedBody': 'The service was not linked. Nothing was shared with it.',
    'goneTitle': 'This link is not available',
    'goneBody':
        'It has expired or was already used. Start again in the service to get a new code.',
    'closedTitle': 'Three wrong codes. The link was closed.',
    'closedBody': 'Start again in the service to get a new code.',
    'cannotShowLink': 'This link cannot be shown. Nothing was linked.',
    'notActive':
        'This device is not active yet, so it cannot link a service.',
    'wrongCode': 'Wrong code.',
    'wrongCodeOne': 'Wrong code. 1 attempt left.',
    'wrongCodeN': 'Wrong code. {n} attempts left.',
    'enterCode': 'Enter the six-digit code.',
    'linkCancelled': 'Linking cancelled. Nothing was sent.',
    'done': 'Done',
    'expiredTitle': 'This link has expired',
    'confirmUnproven':
        'This service has not proven who it is. Press Link and confirm to link it anyway.',
    'linkingOff': 'This build cannot link services.',
    'fromService': 'From {who}:',
    'checkAgain': 'Check again',
    'stillPending': 'Nothing was recorded. The link is still open.',
    'undeterminedTitle': 'The link is no longer open',
    'undeterminedBody':
        'If it went through, the service is now in Settings, Linked services.',
    'dismiss': 'Dismiss',
    'dismissed': 'Dismissed. Nothing was sent.',
  });

  static const sk = RpStrings._({
    'cannotShow': 'Túto žiadosť nemožno zobraziť; aktualizujte aplikáciu',
    'cannotShowBody': 'Nemožno ju tu schváliť. Nič sa neschválilo.',
    'writtenByService': 'Napísala služba',
    'reportedByService': 'Uviedla služba',
    'originLabel': 'Pôvod, ako ho uviedla služba',
    'paymentHeading': 'Potvrďte platbu',
    'payee': 'Príjemca',
    'account': 'Účet',
    'accountNumber': 'Číslo účtu',
    'accountScheme': 'Typ účtu',
    'remittance': 'Referencia',
    'executionDate': 'Dátum vykonania',
    'signInTo': 'Prihlásiť sa do {who}',
    'loginNothing': 'Služba o tomto prihlásení neuviedla nič.',
    'ctxIp': 'Adresa IP',
    'ctxLocation': 'Miesto',
    'ctxUserAgent': 'Prehliadač alebo aplikácia',
    'linkTitle': 'Prepojiť službu',
    'linkAServiceHelp':
        'Naskenujte QR kód, ktorý vám služba ukáže. Prepájanie musíte spustiť vy sami.',
    'pasteCode': 'Vložiť kód prepojenia',
    'pasteField': 'Kód prepojenia',
    'pasteOpen': 'Otvoriť',
    'cancel': 'Zrušiť',
    'notALinkCode': 'Toto nie je kód na prepojenie služby.',
    'differentService': 'Tento kód je pre inú službu autentifikátora',
    'codeProblemTitle': 'Tento kód sa nedá použiť',
    'linkWhat':
        'Táto služba vám bude môcť posielať žiadosti o schválenie. Bez vášho schválenia nemôže nič presunúť.',
    'serviceCallsYou': 'Služba vás volá: {label}',
    'linkButton': 'Prepojiť',
    'dontLink': 'Neprepájať',
    'unprovenTitle': 'Táto služba neoverila, kto je',
    'unprovenBody':
        'Biatec nevie potvrdiť, kto to je. Prepojte ju iba vtedy, ak ste prepájanie spustili sami a službu poznáte.',
    'linkAnyway': 'Aj tak prepojiť',
    'linkedTitle': 'Prepojené.',
    'linkedBody':
        'Táto služba vám teraz môže posielať žiadosti o schválenie. Prepojenie zrušíte v Nastaveniach, Prepojené služby.',
    'deniedTitle': 'Neprepojené.',
    'deniedBody': 'Služba sa neprepojila. Nič sa jej nezdieľalo.',
    'goneTitle': 'Toto prepojenie nie je dostupné',
    'goneBody':
        'Vypršalo alebo sa už použilo. Začnite znova v službe a získajte nový kód.',
    'closedTitle': 'Tri nesprávne kódy. Prepojenie sa zatvorilo.',
    'closedBody': 'Začnite znova v službe a získajte nový kód.',
    'cannotShowLink': 'Toto prepojenie nemožno zobraziť. Nič sa neprepojilo.',
    'notActive':
        'Toto zariadenie ešte nie je aktívne, preto nemôže prepojiť službu.',
    'wrongCode': 'Nesprávny kód.',
    'wrongCodeOne': 'Nesprávny kód. Zostáva 1 pokus.',
    'wrongCodeN': 'Nesprávny kód. Zostáva pokusov: {n}.',
    'enterCode': 'Zadajte šesťmiestny kód.',
    'linkCancelled': 'Prepájanie sa zrušilo. Nič sa neodoslalo.',
    'done': 'Hotovo',
    'expiredTitle': 'Toto prepojenie vypršalo',
    'confirmUnproven':
        'Táto služba neoverila, kto je. Stlačte Prepojiť a potvrďte, ak ju chcete aj tak prepojiť.',
    'linkingOff': 'Táto verzia nemôže prepájať služby.',
    'fromService': 'Od {who}:',
    'checkAgain': 'Skontrolovať znova',
    'stillPending': 'Nič sa nezaznamenalo. Prepojenie je stále otvorené.',
    'undeterminedTitle': 'Prepojenie už nie je otvorené',
    'undeterminedBody':
        'Ak prebehlo, služba je teraz v Nastaveniach, Prepojené služby.',
    'dismiss': 'Zavrieť',
    'dismissed': 'Zavreté. Nič sa neodoslalo.',
  });
}
