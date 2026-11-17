import 'package:flutter_test/flutter_test.dart';

final _placeholder = RegExp(r'\{([a-zA-Z0-9_]+)\}');

/// Checks one group of texts (T-251): the English and the Slovak map have the same keys, no text is empty, the two texts of a key have the same
/// `{placeholders}`, and the Slovak text is not a copy of the English one (except for [sameInBoth], such as a brand name or a code).
void expectSameTexts(String group, Map<String, String> en, Map<String, String> sk, {Set<String> sameInBoth = const {}}) {
  expect(sk.keys.toSet(), en.keys.toSet(), reason: '$group: the Slovak and the English texts have the same keys');
  for (final key in en.keys) {
    final english = en[key]!;
    final slovak = sk[key]!;
    expect(english.trim(), isNotEmpty, reason: '$group.$key (en)');
    expect(slovak.trim(), isNotEmpty, reason: '$group.$key (sk)');
    Set<String> names(String s) => _placeholder.allMatches(s).map((m) => m.group(1)!).toSet();
    expect(names(slovak), names(english), reason: '$group.$key: the same placeholders');
    if (!sameInBoth.contains(key) && english.contains(RegExp('[a-z]{3} [a-z]{3}'))) {
      expect(slovak, isNot(english), reason: '$group.$key: translated');
    }
  }
}
