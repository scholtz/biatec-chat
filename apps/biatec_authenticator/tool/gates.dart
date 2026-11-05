// Source gates for the authenticator, run in CI:
//
//   dart run tool/gates.dart [package folder]
//
//  1. no `print(` or `debugPrint(` in lib/, also not taken as a function value (nothing may log: a code, a token or a key could reach a log);
//  2. no secret-like strings in the app's own source: lib/, android/ (without build output), ios/Runner and pubspec.yaml.
import 'dart:io';

class Finding {
  Finding(this.file, this.line, this.rule);

  final String file;
  final int line;
  final String rule;

  /// Names the place and the rule, never the text that matched (it may be the secret).
  @override
  String toString() => '$file:$line: $rule';
}

/// A call, or the name used as a value (`final log = debugPrint;`, `debugPrint = f;`, `x(print)`).
final _logging = RegExp(
    r'(^|[^A-Za-z0-9_.])(print|debugPrint)\s*(\(|=[^=>]|;|,|\))|[=(,]\s*(print|debugPrint)\s*[;,)]');

/// Strings that look like credentials. Each has a name for the report.
final Map<String, RegExp> secretPatterns = {
  'an AWS access key id': RegExp(r'\bAKIA[0-9A-Z]{16}\b'),
  'a private key block':
      RegExp(r'-----BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
  'a JSON web token':
      RegExp(r'\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.'),
  'a GitHub token': RegExp(r'\bgh[pousr]_[A-Za-z0-9]{36,}\b'),
  'a Slack token': RegExp(r'\bxox[baprs]-[A-Za-z0-9-]{10,}'),
  'a Google API key': RegExp(r'\bAIza[0-9A-Za-z_-]{35}\b'),
  'a credential assigned to a literal': RegExp(
      r'''(api[_-]?key|secret|passw(or)?d|token)["']?\s*[:=]\s*["'][A-Za-z0-9+/_=.-]{20,}["']''',
      caseSensitive: false),
  'a signing password in a properties file': RegExp(
      r'''^\s*(store|key)Password\s*=\s*(?:"[^"$]{8,}"|[A-Za-z0-9+/_=!@%^&*-]{6,}\s*$)''',
      caseSensitive: false),
};

/// Findings in one file's [lines]. Block comments (`/* ... */`) and line comments are not code for the logging rule; the secret rules read the whole line (a key in a comment is still a leaked key).
List<Finding> scan(String path, List<String> lines,
    {required bool checkLogging}) {
  final out = <Finding>[];
  var inBlock = false;
  for (var i = 0; i < lines.length; i++) {
    final line = lines[i];
    var code = line;
    if (inBlock) {
      final end = code.indexOf('*/');
      if (end < 0) {
        code = '';
      } else {
        code = code.substring(end + 2);
        inBlock = false;
      }
    }
    while (true) {
      final start = code.indexOf('/*');
      if (start < 0) break;
      final end = code.indexOf('*/', start + 2);
      if (end < 0) {
        code = code.substring(0, start);
        inBlock = true;
        break;
      }
      code = code.substring(0, start) + code.substring(end + 2);
    }
    final slashes = code.indexOf('//');
    if (slashes >= 0) code = code.substring(0, slashes);
    if (checkLogging && _logging.hasMatch(code)) {
      out.add(Finding(path, i + 1, 'print or debugPrint in lib/'));
    }
    for (final e in secretPatterns.entries) {
      if (e.value.hasMatch(line)) {
        out.add(Finding(path, i + 1, 'looks like ${e.key}'));
      }
    }
  }
  return out;
}

const _sourceExtensions = {
  '.dart',
  '.kt',
  '.swift',
  '.xml',
  '.plist',
  '.yaml',
  '.gradle',
  '.kts',
  '.json',
  '.properties',
  '.entitlements',
  '.pro',
};

/// A developer's own local settings and signing material, kept out of the repository by .gitignore.
const _localOnly = {'local.properties', 'key.properties'};

/// Scans the authenticator under [root] (the package folder), however it is spelled (`.`, `./`, a trailing slash).
List<Finding> scanPackage(String root) {
  final base = root.replaceAll('\\', '/').replaceAll(RegExp(r'/+$'), '');
  final findings = <Finding>[];
  void walk(String rel, {required bool logging}) {
    final path = '$base/$rel';
    final type = FileSystemEntity.typeSync(path);
    if (type == FileSystemEntityType.file) {
      findings
          .addAll(scan(rel, File(path).readAsLinesSync(), checkLogging: false));
      return;
    }
    if (type != FileSystemEntityType.directory) return;
    for (final f
        in Directory(path).listSync(recursive: true).whereType<File>()) {
      final shown = f.path.replaceAll('\\', '/').substring(base.length + 1);
      final name = shown.split('/').last;
      if (RegExp(r'(^|/)(build|\.gradle|Pods|\.dart_tool)/').hasMatch(shown)) {
        continue;
      }
      if (name.startsWith('GeneratedPluginRegistrant.') ||
          _localOnly.contains(name)) {
        continue;
      }
      final dot = name.lastIndexOf('.');
      if (dot < 0 || !_sourceExtensions.contains(name.substring(dot))) {
        continue;
      }
      findings.addAll(scan(shown, f.readAsLinesSync(),
          checkLogging: logging && shown.endsWith('.dart')));
    }
  }

  walk('lib', logging: true);
  walk('android', logging: false);
  walk('ios/Runner', logging: false);
  walk('pubspec.yaml', logging: false);
  return findings;
}

void main(List<String> args) {
  final root = args.isNotEmpty ? args.first : '.';
  final findings = scanPackage(root);
  if (findings.isEmpty) {
    stdout.writeln(
        'gates: no print or debugPrint in lib/, no secret-like strings in the source.');
    return;
  }
  for (final f in findings) {
    stderr.writeln('gates: $f');
  }
  exit(1);
}
