import 'dart:ui';

import 'package:flutter/widgets.dart' show WidgetsBinding, basicLocaleListResolution;

/// The languages of the app (T-251): Slovak on a device set to Slovak, English on any other.
///
/// Every text the user can see comes from a group of texts with an English and a Slovak map of the same keys (`EnrollStrings`, `PinStrings`, and one
/// such class per area of the app). Each group has a test that the two maps have the same keys and the same `{placeholders}`
/// (`test/l10n/texts_support.dart`), and `test/l10n/no_literal_text_test.dart` fails on a sentence written into a widget directly.
const appLocales = [Locale('en'), Locale('sk')];

/// True when the texts are Slovak: [locale], or the device's language when it is null. Code without a `BuildContext` (a controller) asks this as well, so a
/// message it makes is in the same language as the screen that shows it.
bool appLanguageIsSlovak([Locale? locale]) => (locale ?? deviceLocale()).languageCode == 'sk';

/// The language of the app's texts on this device: the device's list of preferred languages resolved against [appLocales], the way `MaterialApp`
/// picks the language of the screens, so a controller's message is in the same language as the screen that shows it (a phone set to Czech, then
/// Slovak, gets Slovak in both). Read through the binding when there is one, so a test that sets the device's languages gets them here too.
Locale deviceLocale() {
  PlatformDispatcher dispatcher;
  try {
    dispatcher = WidgetsBinding.instance.platformDispatcher;
  } on Object {
    dispatcher = PlatformDispatcher.instance;
  }
  final preferred = dispatcher.locales;
  return basicLocaleListResolution(preferred.isEmpty ? [dispatcher.locale] : preferred, appLocales);
}

/// A group of texts in one language: a map from a key to the text. `{name}` in a text is a placeholder that [text] fills in.
abstract class Texts {
  const Texts(this.values);

  /// The texts of this language, by key.
  final Map<String, String> values;

  Iterable<String> get keys => values.keys;

  /// The text of [key], with each `{name}` replaced by the value of `name` in [args].
  String text(String key, [Map<String, Object?> args = const {}]) {
    var value = values[key];
    if (value == null) throw ArgumentError.value(key, 'key', 'no such text');
    args.forEach((name, arg) => value = value!.replaceAll('{$name}', '$arg'));
    return value!;
  }
}
