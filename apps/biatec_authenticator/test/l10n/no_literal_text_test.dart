import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// T-251: a text the user sees is never written into a widget in one language. It comes from a group of texts with an English and a Slovak map
/// (`lib/l10n/texts.dart`). This test reads every file under `lib/` and fails on a word written straight into a `Text`, or into a label, title, tooltip,
/// hint, message, content, subtitle, prompt or semantics label, in single or double quotes, also as either side of `condition ? 'a' : 'b'`.
///
/// What it lets through: an interpolation only (`'$n'`), brand names and technical words that are the same in both languages ([_allowed]), the
/// files that hold the texts themselves, and a line marked `// l10n-ignore: <why>` (a text that is never shown).
void main() {
  test('no sentence is written into a widget directly', () {
    final found = <String>[];
    for (final file in Directory('lib').listSync(recursive: true).whereType<File>()) {
      final path = file.path.replaceAll('\\', '/');
      if (!path.endsWith('.dart') || _textFiles.any(path.endsWith)) continue;
      found.addAll(findLiterals(path, file.readAsStringSync()));
    }
    expect(found, isEmpty, reason: 'Put these texts into a group of texts with an English and a Slovak version:\n${found.join('\n')}');
  });

  test('the guard sees a literal it must refuse, and lets through what it must', () {
    List<String> scan(String source) => findLiterals('x.dart', source);
    expect(scan("Text('Set up this device')"), hasLength(1));
    expect(scan('Text("Set up this device")'), hasLength(1), reason: 'double quotes');
    expect(scan("title: 'Device inactive',"), hasLength(1));
    expect(scan("tooltip:\n    'Show the code'"), hasLength(1));
    expect(scan("content: Text('Copied')"), hasLength(1));
    expect(scan("subtitle: Text(busy ? 'Checking now' : 'Ready')"), hasLength(2), reason: 'both sides of a condition');
    expect(scan("Text('Open https://example.org now')"), hasLength(1), reason: 'a // inside a string is not a comment');
    expect(scan("// Text('in a comment')"), isEmpty);
    expect(scan("x();\nText('Second line')").single, startsWith('x.dart:2 '));
    expect(scan("Text('Shown') // l10n-ignore: test"), isEmpty);
    expect(scan(r"Text('$n')"), isEmpty);
    expect(scan(r"Text('${a.b} · ${c}')"), isEmpty);
    expect(scan("Text('Biatec Authenticator')"), isEmpty);
    expect(scan("Text(t.text('key'))"), isEmpty);
  });
}

/// The files that hold the texts.
const _textFiles = ['_strings.dart', '_texts.dart', 'l10n/texts.dart'];

/// Words that are the same in English and Slovak (brand names, technical words).
const _allowed = {'Biatec Authenticator', 'Biatec', 'OIDC', 'TOTP', 'HOTP', 'QR', 'PIN', 'TPM', 'ID', 'SHA1', 'SHA256', 'SHA512', 'otpauth'};

const _names =
    'label|labelText|title|tooltip|hintText|helperText|errorText|message|actionLabel|retryLabel|semanticLabel|semanticsLabel|confirmLabel|cancelLabel|content|subtitle|reason|promptTitle|promptSubtitle';

/// A quoted string: group `q` is the quote, the text follows until the same quote.
const _string = r"""(?:'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)")""";

/// Where a shown text starts: `Text(` or one of the named arguments, possibly through `const Text(` / `Text(`.
final _start = RegExp('(Text\\(|(?:$_names):)\\s*(?:const\\s+)?(?:Text\\()?\\s*');

/// The sentences shown in [source] (named [path] in the result), as `path:line argument 'text'`.
List<String> findLiterals(String path, String source) {
  final lines = source.replaceAll('\r', '').split('\n');
  final code = lines.map(_withoutComment).join('\n');
  final found = <String>[];
  for (final start in _start.allMatches(code)) {
    // The value: a string, or a condition whose two sides are strings.
    final rest = code.substring(start.end);
    final values = <String>[];
    final plain = RegExp('^$_string').firstMatch(rest);
    if (plain != null) {
      values.add(plain.group(1) ?? plain.group(2)!);
    } else {
      final condition = RegExp('^[^,;()\\n?]*\\?\\s*$_string\\s*:\\s*$_string').firstMatch(rest);
      if (condition != null) {
        values.add(condition.group(1) ?? condition.group(2)!);
        values.add(condition.group(3) ?? condition.group(4)!);
      }
    }
    final line = '\n'.allMatches(code.substring(0, start.start)).length + 1;
    // A text that is never shown (an exception's message for the log) is marked on its line with `l10n-ignore` and why.
    if (lines[line - 1].contains('l10n-ignore')) continue;
    for (final value in values.where(_isWords)) {
      found.add('$path:$line ${start.group(1)} \'$value\'');
    }
  }
  return found;
}

bool _isWords(String value) {
  final text = value.replaceAll(RegExp(r'\$\{[^}]*\}|\$[a-zA-Z_]\w*'), ' ').trim();
  if (_allowed.contains(text)) return false;
  final words = RegExp(r'[A-Za-zÀ-ž]{2,}').allMatches(text).map((m) => m.group(0)!).where((w) => !_allowed.contains(w));
  return words.isNotEmpty;
}

/// The line without a `//` comment; a `//` inside a string stays.
String _withoutComment(String line) {
  String? quote;
  for (var i = 0; i < line.length; i++) {
    final c = line[i];
    if (quote != null) {
      if (c == '\\') {
        i++;
      } else if (c == quote) {
        quote = null;
      }
    } else if (c == "'" || c == '"') {
      quote = c;
    } else if (c == '/' && i + 1 < line.length && line[i + 1] == '/') {
      return line.substring(0, i);
    }
  }
  return line;
}
