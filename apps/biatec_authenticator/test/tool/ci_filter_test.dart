// T-220: the authenticator CI jobs build a signed release APK, so a pull request that does not touch the app does not wait for them. The
// workflow is read as text (flutter test runs from the package root); the shell logic itself runs in CI.
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  final ci = File('../../.github/workflows/ci.yml').readAsStringSync();

  /// The text of one job: from its name to the next job (jobs sit at two spaces of indent under `jobs:`).
  String job(String name) {
    final heads =
        RegExp(r'^  ([a-z-]+):\s*$', multiLine: true).allMatches(ci).toList();
    final i = heads.indexWhere((m) => m.group(1) == name);
    expect(i, isNonNegative, reason: 'job $name exists');
    return ci.substring(
        heads[i].start, i + 1 < heads.length ? heads[i + 1].start : ci.length);
  }

  test('both authenticator jobs wait for the change check and can be skipped',
      () {
    for (final name in ['authenticator', 'authenticator-security']) {
      final text = job(name);
      expect(text, contains('needs: changes'), reason: name);
      expect(text, contains("needs.changes.outputs.authenticator == 'true'"),
          reason: name);
    }
  });

  test('the other jobs never wait for it', () {
    for (final name in ['backend', 'js', 'images', 'deploy-rbac']) {
      expect(job(name), isNot(contains('needs: changes')), reason: name);
    }
  });

  test('everything that is not a pull request runs the authenticator jobs', () {
    final text = job('changes');
    expect(text, contains(r'"$EVENT_NAME" != "pull_request"'));
    expect(text, contains('authenticator=true'));
  });

  test('the app, its protocol and mock, its documents and this file count', () {
    final text = job('changes');
    for (final path in [
      'apps/biatec_authenticator/',
      'packages/consent-protocol/',
      'AUTHENTICATOR-',
      'threat-model',
      'ci',
    ]) {
      expect(text, contains(path));
    }
    final pattern = RegExp(r"grep -Eq '([^']+)'").firstMatch(text)!.group(1)!;
    // The shell pattern is a POSIX extended expression that Dart reads the same way.
    final re = RegExp(pattern);
    for (final changed in [
      'apps/biatec_authenticator/lib/main.dart',
      'packages/consent-protocol/mock-oidc/src/index.js',
      'docs/AUTHENTICATOR-SECURITY.md',
      'docs/threat-model.md',
      '.github/workflows/ci.yml',
    ]) {
      expect(re.hasMatch(changed), isTrue, reason: changed);
    }
    for (final changed in [
      'apps/backend/Program.cs',
      'apps/admin/src/main.ts',
      '.github/workflows/deploy.yml',
      'README.md',
    ]) {
      expect(re.hasMatch(changed), isFalse, reason: changed);
    }
  });
}
