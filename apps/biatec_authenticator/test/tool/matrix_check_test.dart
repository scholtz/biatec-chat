import 'dart:io';

import '../../tool/matrix_check.dart';
import 'package:flutter_test/flutter_test.dart';

const docs = '../../docs';
const tasks = '../../kanban/tasks';

String realMatrix() =>
    File('$docs/AUTHENTICATOR-SECURITY.md').readAsStringSync();

/// A matrix with one row per control, all `met` and linking this test file, so a test can break one thing at a time.
String sound({Map<String, String> replace = const {}}) {
  final rows = [
    for (final c in masvsControls)
      replace[c] ??
          '| $c | text | met | [test](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | note |'
  ];
  return '| Control | Requirement | Status | Evidence | Notes |\n| --- | --- | --- | --- | --- |\n${rows.join('\n')}\n';
}

List<String> check(String md) =>
    checkMatrix(md, docsDir: docs, tasksDir: tasks);

void main() {
  group('the real matrix', () {
    test('has every control with a status and real evidence', () {
      expect(check(realMatrix()), isEmpty);
    });

    test('lists all 24 MASVS v2.0 controls once', () {
      expect(masvsControls.length, 24);
      expect(masvsControls.toSet().length, 24);
      final rows = parseMatrix(realMatrix());
      expect([for (final r in rows) r.control]..sort(),
          [...masvsControls]..sort());
    });

    test('names every open item with an owner or a task', () {
      final text = realMatrix();
      final open =
          text.split('## Open items').last.split('## How this document').first;
      final lines =
          open.split('\n').where((l) => RegExp(r'^\| O-\d+').hasMatch(l));
      expect(lines.length, greaterThanOrEqualTo(8));
      for (final l in lines) {
        expect(l.contains('owner:') || RegExp(r'T-\d{3}').hasMatch(l), isTrue,
            reason: l);
      }
    });

    test('the documents it points at exist', () {
      for (final f in [
        'AUTHENTICATOR-PENTEST-BRIEF.md',
        'AUTHENTICATOR-RELEASE-CHECKLIST.md',
        'threat-model.md',
        'AUTHENTICATOR-PINNING.md'
      ]) {
        expect(File('$docs/$f').existsSync(), isTrue, reason: f);
      }
    });
  });

  group('the checker fails on a seeded violation', () {
    test('a sound matrix passes', () {
      expect(check(sound()), isEmpty);
    });

    test('a missing control', () {
      final md = sound()
          .split('\n')
          .where((l) => !l.contains('MASVS-CRYPTO-2'))
          .join('\n');
      expect(check(md), contains('MASVS-CRYPTO-2: missing from the matrix'));
    });

    test('a control twice', () {
      final md =
          '${sound()}| MASVS-AUTH-1 | again | met | [t](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | n |\n';
      expect(check(md).any((p) => p.contains('MASVS-AUTH-1: appears twice')),
          isTrue);
    });

    test('a control that is not in MASVS', () {
      final md =
          '${sound()}| MASVS-MAGIC-9 | x | met | [t](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | n |\n';
      expect(
          check(md).any((p) => p.contains('not a MASVS v2.0 control')), isTrue);
    });

    test('a status that is not one of the four', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | mostly | [t](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | n |'
      });
      expect(check(md).any((p) => p.contains('MASVS-AUTH-1: status "mostly"')),
          isTrue);
    });

    test('met with no link', () {
      final md = sound(replace: {
        'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | met | trust me | n |'
      });
      expect(
          check(md),
          contains(
              'MASVS-AUTH-1: met, but no evidence link to a file under apps/ or .github/ (code, a test or a workflow)'));
    });

    test('a link to a file that does not exist', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | met | [gone](../apps/biatec_authenticator/lib/nowhere.dart) | n |'
      });
      expect(check(md).any((p) => p.contains('does not exist')), isTrue);
    });

    test('partly without the missing part named', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | partly | [t](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | some is missing |'
      });
      expect(check(md).any((p) => p.contains('no task id or owner')), isTrue);
    });

    test('open without a task or owner', () {
      final md = sound(
          replace: {'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | open | | later |'});
      expect(
          check(md), contains('MASVS-AUTH-1: open, with no task id or owner'));
    });

    test('open naming a task that has no file', () {
      final md = sound(
          replace: {'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | open | | T-999 |'});
      expect(check(md),
          contains('MASVS-AUTH-1: names T-999, which has no task file'));
    });

    test('met linking only another document is not evidence', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | met | [doc](AUTHENTICATOR-PINNING.md) | n |'
      });
      expect(
          check(md)
              .any((p) => p.contains('no evidence link to a file under apps/')),
          isTrue);
    });

    test('met linking a folder is not evidence', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | met | [dir](../apps/biatec_authenticator/test/tool) | n |'
      });
      expect(
          check(md)
              .any((p) => p.contains('no evidence link to a file under apps/')),
          isTrue);
    });

    test(
        'a relative link that starts with http is a file link, not an external one',
        () {
      final md = sound(replace: {
        'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | met | [x](http-notes.md) | n |'
      });
      expect(check(md).any((p) => p.contains('"http-notes.md" does not exist')),
          isTrue);
    });

    test('an external link is not looked up on disk', () {
      final md = sound(replace: {
        'MASVS-AUTH-1':
            '| MASVS-AUTH-1 | t | met | [a](../apps/biatec_authenticator/test/tool/matrix_check_test.dart), [b](https://example.com/x) | n |'
      });
      expect(check(md), isEmpty);
    });

    test('an escaped pipe in a cell does not shift the columns', () {
      final cells =
          splitRow(r'| MASVS-AUTH-1 | a \| b | met | [t](x.md) | n \| m |');
      expect(cells, ['MASVS-AUTH-1', 'a | b', 'met', '[t](x.md)', 'n | m']);
      final md = sound(replace: {
        'MASVS-AUTH-1':
            r'| MASVS-AUTH-1 | a \| b | met | [t](../apps/biatec_authenticator/test/tool/matrix_check_test.dart) | n \| m |'
      });
      expect(check(md), isEmpty);
    });

    test('a row without the closing pipe keeps its last cell whole', () {
      expect(splitRow('| MASVS-AUTH-1 | t | met | e | notes end'),
          ['MASVS-AUTH-1', 't', 'met', 'e', 'notes end']);
    });

    test('open with an owner is accepted', () {
      final md = sound(replace: {
        'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | open | | owner: user |'
      });
      expect(check(md), isEmpty);
    });

    test('not applicable without a reason', () {
      final md = sound(replace: {
        'MASVS-AUTH-1': '| MASVS-AUTH-1 | t | not applicable | | |'
      });
      expect(
          check(md), contains('MASVS-AUTH-1: not applicable, with no reason'));
    });
  });
}
