import 'dart:ui';

import 'package:flutter/widgets.dart' show WidgetsBinding;

/// The languages of the app (T-251): Slovak on a device set to Slovak, English on any other.
///
/// Every text the user can see comes from a group of texts with an English and a Slovak map of the same keys (`EnrollStrings`, `PinStrings`, and one
/// such class per area of the app). Each group has a test that the two maps have the same keys and the same `{placeholders}`
/// (`test/l10n/texts_support.dart`), and `test/l10n/no_literal_text_test.dart` fails on a sentence written into a widget directly.
const appLocales = [Locale('en'), Locale('sk')];

/// True when the texts are Slovak: [locale], or the device's language when it is null. Code without a `BuildContext` (a controller) asks this as well, so a
/// message it makes is in the same language as the screen that shows it.
bool appLanguageIsSlovak([Locale? locale]) => (locale ?? deviceLocale()).languageCode == 'sk';

/// The device's language, read through the binding when there is one: a test that sets `tester.platformDispatcher.localeTestValue` then gets the same
/// language here as on its screens. Without a binding (a plain Dart test), the platform's own.
Locale deviceLocale() {
  try {
    return WidgetsBinding.instance.platformDispatcher.locale;
  } on Object {
    return PlatformDispatcher.instance.locale;
  }
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
