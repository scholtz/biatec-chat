import 'dart:ui';

/// The texts of the device channel: the "Stay connected for approvals" setting, the notifications and the service's notification. English and Slovak, chosen by the phone's language (the rest of the app is English only for now). Notification texts are fixed here; nothing from Biatec OIDC goes into them.
class ChannelStrings {
  const ChannelStrings._(this._t);

  /// The strings for [locale] (the phone's language when null): Slovak for `sk`, English otherwise.
  factory ChannelStrings.of([Locale? locale]) {
    final language =
        (locale ?? PlatformDispatcher.instance.locale).languageCode;
    return language == 'sk' ? sk : en;
  }

  final Map<String, String> _t;

  String get stayTitle => _t['stayTitle']!;
  String get stayOff => _t['stayOff']!;
  String get stayOn => _t['stayOn']!;
  String get stayBattery => _t['stayBattery']!;
  String get batteryTitle => _t['batteryTitle']!;
  String get batteryBody => _t['batteryBody']!;
  String get batteryOpen => _t['batteryOpen']!;
  String get notNow => _t['notNow']!;

  /// Notification: a request is waiting.
  String get requestTitle => _t['requestTitle']!;
  String get requestBody => _t['requestBody']!;

  /// Notification: a device was added.
  String get deviceTitle => _t['deviceTitle']!;
  String get deviceBody => _t['deviceBody']!;

  /// The persistent notification of the Android service.
  String get serviceTitle => _t['serviceTitle']!;
  String get serviceBody => _t['serviceBody']!;
  String get serviceChannel => _t['serviceChannel']!;
  String get approvalsChannel => _t['approvalsChannel']!;

  /// Every key, so a test can check that both languages have all of them.
  Iterable<String> get keys => _t.keys;

  static const en = ChannelStrings._({
    'stayTitle': 'Stay connected for approvals',
    'stayOff':
        'Off: you will see requests when you open the app. Nothing can wake the app while it is closed.',
    'stayOn':
        'On: the app keeps a connection to Biatec OIDC and shows a notification as soon as a request arrives, also when the app is closed.',
    'stayBattery':
        'This keeps the app running in the background with a permanent notification, which uses battery.',
    'batteryTitle': 'Allow background use',
    'batteryBody':
        'To stay connected, the app should be exempt from battery optimisation. You can change this later in the system settings.',
    'batteryOpen': 'Open settings',
    'notNow': 'Not now',
    'requestTitle': 'Approval requested',
    'requestBody': 'Open Biatec Authenticator to review',
    'deviceTitle': 'Device added',
    'deviceBody': 'Open Biatec Authenticator to review',
    'serviceTitle': 'Biatec Authenticator',
    'serviceBody': 'Connected, waiting for approval requests',
    'serviceChannel': 'Stay connected',
    'approvalsChannel': 'Approval requests',
  });

  static const sk = ChannelStrings._({
    'stayTitle': 'Zostať pripojený kvôli schváleniam',
    'stayOff':
        'Vypnuté: žiadosti uvidíte, keď aplikáciu otvoríte. Zatvorenú aplikáciu nič nedokáže zobudiť.',
    'stayOn':
        'Zapnuté: aplikácia drží spojenie s Biatec OIDC a hneď po príchode žiadosti zobrazí upozornenie, aj keď je zatvorená.',
    'stayBattery':
        'Aplikácia tak beží na pozadí so stálym upozornením, čo míňa batériu.',
    'batteryTitle': 'Povoliť beh na pozadí',
    'batteryBody':
        'Aby zostala pripojená, aplikácia by mala byť vyňatá z optimalizácie batérie. Neskôr to môžete zmeniť v nastaveniach systému.',
    'batteryOpen': 'Otvoriť nastavenia',
    'notNow': 'Teraz nie',
    'requestTitle': 'Žiadosť o schválenie',
    'requestBody': 'Otvorte Biatec Authenticator a skontrolujte ju',
    'deviceTitle': 'Pridané zariadenie',
    'deviceBody': 'Otvorte Biatec Authenticator a skontrolujte ho',
    'serviceTitle': 'Biatec Authenticator',
    'serviceBody': 'Pripojené, čaká na žiadosti o schválenie',
    'serviceChannel': 'Zostať pripojený',
    'approvalsChannel': 'Žiadosti o schválenie',
  });
}
