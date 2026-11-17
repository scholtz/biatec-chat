import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// T-251: a text the user sees is never written into a widget in one language. It comes from a group of texts with an English and a Slovak map
/// (`lib/l10n/texts.dart`). This test reads every file under `lib/` and fails on a word written straight into a `Text`, or into a label, title, tooltip,
/// hint, message or semantics label.
///
/// What it lets through: an interpolation only (`'$n'`), brand names and technical words that are the same in both languages ([_allowed]), and the
/// files that hold the texts themselves, and a line marked `// l10n-ignore: <why>` (a text that is never shown).
void main() {
  test('no sentence is written into a widget directly', () {
    final found = <String>[];
    for (final file in Directory('lib').listSync(recursive: true).whereType<File>()) {
      final path = file.path.replaceAll('\\', '/');
      if (!path.endsWith('.dart') || _textFiles.any(path.endsWith)) continue;
      final source = file.readAsStringSync().replaceAll('\r', '');
      for (final match in _literal.allMatches(_withoutComments(source))) {
        final value = match.group(2)!;
        if (!_isWords(value)) continue;
        final line = '\n'.allMatches(source.substring(0, source.indexOf(match.group(0)!) + 1)).length + 1;
        // A text that is never shown (an exception's message for the log) is marked on its line with `l10n-ignore` and why.
        if (source.split('\n')[line - 1].contains('l10n-ignore')) continue;
        found.add('$path:$line ${match.group(1)} \'$value\'');
      }
    }
    expect(found, isEmpty, reason: 'Put these texts into a group of texts with an English and a Slovak version:\n${found.join('\n')}');
  });

  test('the guard sees a literal it must refuse, and lets through what it must', () {
    expect(_literal.firstMatch("Text('Set up this device')"), isNotNull);
    expect(_literal.firstMatch("title: 'Device inactive',"), isNotNull);
    expect(_literal.firstMatch("tooltip:\n    'Show the code'"), isNotNull);
    expect(_isWords('Set up this device'), isTrue);
    expect(_isWords(r'$n'), isFalse);
    expect(_isWords(r'${a.b} · ${c}'), isFalse);
    expect(_isWords('Biatec Authenticator'), isFalse);
    expect(_isWords('·'), isFalse);
  });
}

/// The files that hold the texts.
const _textFiles = ['_strings.dart', '_texts.dart', 'l10n/texts.dart', 'security/pin_prompt.dart', 'keys/key_security_level.dart'];

/// Words that are the same in English and Slovak (brand names, technical words).
const _allowed = {'Biatec Authenticator', 'Biatec', 'OIDC', 'TOTP', 'HOTP', 'QR', 'PIN', 'TPM', 'ID', 'SHA1', 'SHA256', 'SHA512', 'otpauth'};

/// A string literal given to a widget text or to a named argument that is shown.
final _literal = RegExp(
    r"""(Text\(|(?:label|labelText|title|tooltip|hintText|helperText|errorText|message|actionLabel|retryLabel|semanticLabel|semanticsLabel|confirmLabel|cancelLabel):)\s*(?:const\s+Text\()?\s*'((?:[^'\\\n]|\\.)*)'""");

bool _isWords(String value) {
  final text = value.replaceAll(RegExp(r'\$\{[^}]*\}|\$[a-zA-Z_]\w*'), ' ').trim();
  if (_allowed.contains(text)) return false;
  final words = RegExp(r'[A-Za-zÀ-ž]{2,}').allMatches(text).map((m) => m.group(0)!).where((w) => !_allowed.contains(w));
  return words.isNotEmpty;
}

String _withoutComments(String source) => source.replaceAll(RegExp(r'//[^\n]*'), '');
