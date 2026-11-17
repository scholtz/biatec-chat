import 'package:biatec_authenticator/clients/trust_strings.dart';
import 'package:biatec_authenticator/devices/devices_texts.dart';
import 'package:biatec_authenticator/privacy/privacy_texts.dart';
import 'package:biatec_authenticator/privacy/storage_inventory.dart';
import 'package:biatec_authenticator/security/security_texts.dart';
import 'package:biatec_authenticator/settings/channel_strings.dart';
import 'package:biatec_authenticator/settings/settings_texts.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'texts_support.dart';

void main() {
  test('the settings texts have the same keys and placeholders in English and Slovak', () {
    expectSameTexts('SettingsTexts', SettingsTexts.en.values, SettingsTexts.sk.values);
    expectSameTexts('SecurityTexts', SecurityTexts.en.values, SecurityTexts.sk.values);
    expectSameTexts('DevicesTexts', DevicesTexts.en.values, DevicesTexts.sk.values);
    expectSameTexts('PrivacyTexts', PrivacyTexts.en.values, PrivacyTexts.sk.values);
    expectSameTexts('TrustStrings', TrustStrings.en.values, TrustStrings.sk.values);
    expectSameTexts('ChannelStrings', ChannelStrings.en.values, ChannelStrings.sk.values);
  });

  test('a language is chosen by the locale, English for any other', () {
    expect(SettingsTexts.of(const Locale('sk')), same(SettingsTexts.sk));
    expect(SettingsTexts.of(const Locale('de')), same(SettingsTexts.en));
    expect(SecurityTexts.of(const Locale('sk')).text('done'), 'Hotovo');
    expect(DevicesTexts.of(const Locale('en')).text('title'), 'Device management');
  });

  test('every item of the privacy inventory has its texts', () {
    for (final i in [...storedOnPhone, ...sentToBiatec, ...sentToPushService]) {
      for (final locale in const [Locale('en'), Locale('sk')]) {
        expect(i.titleIn(locale), isNotEmpty, reason: i.id);
        expect(i.textIn(locale), isNotEmpty, reason: i.id);
      }
    }
    for (final key in neverCollected) {
      expect(PrivacyTexts.sk.text(key), isNot(PrivacyTexts.en.text(key)));
    }
  });
}
