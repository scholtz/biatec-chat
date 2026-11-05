// Source gates for the authenticator, run in CI:
//
//   dart run tool/gates.dart
//
//  1. no `print(` or `debugPrint(` in lib/ (nothing may log: a code, a token or a key could reach a log);
//  2. no secret-like strings in the app's own source (lib/, android/app/src, ios/Runner, pubspec.yaml).
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

final _logging = RegExp(r'(^|[^A-Za-z0-9_.])(print|debugPrint)\s*\(');

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
      r'''(api[_-]?key|secret|passw(or)?d|token)["']?\s*[:=]\s*["'][A-Za-z0-9+/_=-]{20,}["']''',
      caseSensitive: false),
};

bool _isComment(String line) {
  final t = line.trimLeft();
  return t.startsWith('//') || t.startsWith('*') || t.startsWith('/*');
}

/// Findings in one file's [lines].
List<Finding> scan(String path, List<String> lines,
    {required bool checkLogging}) {
  final out = <Finding>[];
  for (var i = 0; i < lines.length; i++) {
    final line = lines[i];
    if (checkLogging && !_isComment(line) && _logging.hasMatch(line)) {
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
  '.json'
};

/// Scans the authenticator under [root] (the package folder).
List<Finding> scanPackage(String root) {
  final findings = <Finding>[];
  void walk(String rel, {required bool logging}) {
    final dir = Directory('$root/$rel');
    final entity = FileSystemEntity.typeSync('$root/$rel');
    if (entity == FileSystemEntityType.file) {
      findings.addAll(
          scan(rel, File('$root/$rel').readAsLinesSync(), checkLogging: false));
      return;
    }
    if (!dir.existsSync()) return;
    for (final f in dir.listSync(recursive: true).whereType<File>()) {
      final path = f.path.replaceAll('\\', '/');
      final shown = path.substring(root.length + 1);
      if (shown.contains('/build/') ||
          shown.contains('/.gradle/') ||
          shown.contains('/Pods/')) {
        continue;
      }
      if (shown.endsWith('GeneratedPluginRegistrant.java') ||
          shown.endsWith('GeneratedPluginRegistrant.m') ||
          shown.endsWith('GeneratedPluginRegistrant.h')) {
        continue;
      }
      final dot = shown.lastIndexOf('.');
      if (dot < 0 || !_sourceExtensions.contains(shown.substring(dot))) {
        continue;
      }
      findings.addAll(scan(shown, f.readAsLinesSync(),
          checkLogging: logging && shown.endsWith('.dart')));
    }
  }

  walk('lib', logging: true);
  walk('android/app/src', logging: false);
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
