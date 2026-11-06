/// One thing the app keeps on the phone, or sends, in words for the user.
class DataItem {
  const DataItem(this.title, this.text, {this.keys = const []});

  final String title;
  final String text;

  /// The names it is stored under in the secure storage. A test checks that every storage key in the source is listed here, so a new one cannot be added without telling the user.
  final List<String> keys;
}

/// What the app stores on this phone.
const storedOnPhone = [
  DataItem(
    'Your two-factor accounts',
    'The service, the account name and the settings of each account are in a '
        'file in the app\'s private folder. The secret key of each one is '
        'encrypted with a key that never leaves the phone\'s security chip and '
        'opens only with your fingerprint or face. The keys are not shown '
        'again and are not in any backup.',
    keys: ['biatec.vault.version'],
  ),
  DataItem(
    'The device key',
    'The key that signs your approvals. It is made inside the security chip '
        'and cannot be copied out.',
  ),
  DataItem(
    'Your enrollment',
    'The identifier of this device and the token it uses to talk to Biatec '
        'OIDC, in the phone\'s secure storage.',
    keys: ['biatec.enrollment', 'biatec.enrollment.alias'],
  ),
  DataItem(
    'Your approval history',
    'What you approved or denied, with the amount, who asked and when, so you '
        'can read the receipt later. Nothing that would let someone else act '
        'on it.',
    keys: ['biatec.approvals.history'],
  ),
  DataItem(
    'Your settings',
    'The light or dark choice, when the app locks, whether codes are hidden '
        'until tapped, and the latest time the app showed codes at.',
    keys: [
      'biatec.theme',
      'biatec.lock.delay',
      'biatec.codes.tapToReveal',
      'biatec.codes.latestSeen',
    ],
  ),
  DataItem(
    'Update information',
    'The oldest version of the app that Biatec OIDC still accepts, and a '
        'marker that says the storage was upgraded.',
    keys: ['biatec.minimumBuild', 'biatec.storage.v2'],
  ),
];

/// What the app sends to Biatec OIDC.
const sentToBiatec = [
  DataItem(
    'When you enroll',
    'The public half of the device key and, where the phone offers it, proof '
        'from the phone that this is the real app.',
  ),
  DataItem(
    'With every request',
    'The device token and a signature made with the device key, so Biatec '
        'OIDC knows the call comes from this phone.',
  ),
  DataItem(
    'Your decisions',
    'The approval or denial you give, signed, and a change to your security '
        'settings.',
  ),
  DataItem(
    'The notification address',
    'The token your phone\'s push service gave this app, so Biatec OIDC can '
        'tell you that a request is waiting.',
  ),
];

/// What the push service (Google or Apple) sees.
const sentToPushService = [
  DataItem(
    'A notice that something is waiting',
    'The push service delivers it to this phone. It carries no amount, name or '
        'code; the app fetches the request from Biatec OIDC when you open it.',
  ),
];

/// What the app does not collect.
const neverCollected = [
  'Your two-factor secret keys and codes: they never leave the phone.',
  'Pictures: the camera is used only while you scan a QR code, and nothing is recorded.',
  'Your location, contacts, photos or files.',
  'An advertising identifier, analytics or usage statistics. The app has no analytics and no advertising.',
];
