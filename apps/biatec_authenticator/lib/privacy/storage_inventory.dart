import 'dart:ui';

import 'privacy_texts.dart';

/// One thing the app keeps on the phone, or sends, in words for the user: its title and its text are the [PrivacyTexts] `<id>.title` and `<id>.text`.
class DataItem {
  const DataItem(this.id, {this.keys = const []});

  /// The key of its texts in [PrivacyTexts].
  final String id;

  /// The names it is stored under in the secure storage. A test checks that every storage key in the source is listed here, so a new one cannot be added without telling the user.
  final List<String> keys;

  /// The title in the language of [locale] (the phone's when null).
  String titleIn([Locale? locale]) => PrivacyTexts.of(locale).text('$id.title');

  /// The text in the language of [locale] (the phone's when null).
  String textIn([Locale? locale]) => PrivacyTexts.of(locale).text('$id.text');

  /// The title in the phone's language.
  String get title => titleIn();

  /// The text in the phone's language.
  String get text => textIn();
}

/// What the app stores on this phone.
const storedOnPhone = [
  DataItem('accounts', keys: ['biatec.vault.version']),
  DataItem('deviceKey'),
  DataItem('enrollment', keys: ['biatec.enrollment', 'biatec.enrollment.alias']),
  DataItem('history', keys: ['biatec.approvals.history']),
  DataItem('settings', keys: [
    'biatec.theme',
    'biatec.lock.delay',
    'biatec.codes.tapToReveal',
    'biatec.codes.latestSeen',
  ]),
  DataItem('update', keys: ['biatec.minimumBuild', 'biatec.storage.v2']),
];

/// What the app sends to Biatec OIDC.
const sentToBiatec = [
  DataItem('enroll'),
  DataItem('request'),
  DataItem('decisions'),
  DataItem('pushAddress'),
];

/// What the push service (Apple, in a build with the optional wake) sees. There is no other push service.
const sentToPushService = [
  DataItem('notice'),
];

/// What the app does not collect: the keys of the sentences in [PrivacyTexts].
const neverCollected = [
  'neverSecrets',
  'neverPictures',
  'neverPersonal',
  'neverAnalytics',
];
