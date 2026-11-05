import 'dart:io';

import '../../tool/gates.dart';
import 'package:flutter_test/flutter_test.dart';

List<Finding> inLib(String code) =>
    scan('lib/x.dart', code.split('\n'), checkLogging: true);

void main() {
  group('the real source', () {
    test('has no logging in lib/ and no secret-like string', () {
      expect(scanPackage('.'), isEmpty);
    });
  });

  group('the logging gate fails on a seeded violation', () {
    test('print and debugPrint', () {
      expect(inLib("void f() { print('x'); }").single.rule, contains('print'));
      expect(inLib("  debugPrint('x');").length, 1);
      expect(inLib('final a = 1;\nprint(a);').single.line, 2);
    });

    test('but not a word that merely contains print, nor a comment', () {
      expect(inLib('final fingerprint = 1;'), isEmpty);
      expect(inLib('blueprint(1);'), isEmpty);
      expect(inLib('x.print(1);'), isEmpty);
      expect(inLib('// print(x) would log'), isEmpty);
      expect(inLib('/// do not debugPrint(code)'), isEmpty);
    });

    test('the report names the place and the rule, not the text', () {
      final f = inLib("print('secret-value-123');").single;
      expect(f.toString(), isNot(contains('secret-value-123')));
    });

    test('a seeded file in a copy of the package is found', () {
      final dir = Directory.systemTemp.createTempSync('gates');
      addTearDown(() => dir.deleteSync(recursive: true));
      Directory('${dir.path}/lib').createSync();
      File('${dir.path}/lib/ok.dart').writeAsStringSync('void ok() {}\n');
      expect(scanPackage(dir.path), isEmpty);
      File('${dir.path}/lib/bad.dart')
          .writeAsStringSync("void bad() { print('x'); }\n");
      final found = scanPackage(dir.path);
      expect(found.single.file, 'lib/bad.dart');
    });
  });

  group('the secret gate fails on a seeded violation', () {
    final seeded = {
      'an AWS key': 'const k = "AKIAABCDEFGHIJKLMNOP";',
      'a private key': '-----BEGIN PRIVATE KEY-----',
      'a JWT':
          'const t = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc";',
      'a GitHub token': 'const t = "ghp_${'a' * 36}";',
      'a Slack token': 'const t = "xoxb-1234567890-abcdefghij";',
      'a Google key': 'const k = "AIza${'a' * 35}";',
      'a literal credential':
          "const apiKey = 'abcdefghijklmnopqrstuvwxyz012345';",
      'a password literal': 'password: "abcdefghijklmnopqrstuvwxyz"',
    };
    for (final e in seeded.entries) {
      test(e.key, () {
        final f = scan('lib/x.dart', [e.value], checkLogging: false);
        expect(f, isNotEmpty);
        expect(f.first.toString(), isNot(contains(e.value)));
      });
    }

    test('ordinary code and short values pass', () {
      for (final ok in [
        "const label = 'Account name';",
        "const token = 'x';",
        "final password = controller.text;",
        "const storageKey = 'biatec.vault.version';",
      ]) {
        expect(scan('lib/x.dart', [ok], checkLogging: false), isEmpty,
            reason: ok);
      }
    });

    test('a seeded secret in the native source is found', () {
      final dir = Directory.systemTemp.createTempSync('gates');
      addTearDown(() => dir.deleteSync(recursive: true));
      Directory('${dir.path}/android/app/src').createSync(recursive: true);
      File('${dir.path}/android/app/src/Key.kt')
          .writeAsStringSync('val k = "AKIAABCDEFGHIJKLMNOP"\n');
      expect(scanPackage(dir.path).single.file, 'android/app/src/Key.kt');
    });
  });
}
