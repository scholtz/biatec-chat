// T-247: the Dart port of the relying-party text profile (spec chapter 15, `isRelyingPartyText` of the protocol kit) against vectors/text-rules.json, which the kit and the .NET reference
// test use too. The label profile (the user's own text, T-245) is checked against the same file for the cases it lists.
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/clients/label_rules.dart';
import 'package:biatec_authenticator/text/text_rules.dart';
import 'package:flutter_test/flutter_test.dart';

List<Map<String, Object?>> _vectors() => [
      for (final v in (jsonDecode(File(
                      '../../packages/consent-protocol/vectors/text-rules.json')
                  .readAsStringSync()) as Map)['vectors'] as List)
        (v as Map).cast<String, Object?>()
    ];

void main() {
  final all = _vectors();

  test('the vector file is read (not an empty loop)', () {
    expect(all.length, greaterThan(50));
    expect(all.where((v) => v['profile'] == 'relying_party').length,
        greaterThan(50));
  });

  group('relying-party profile', () {
    for (final v in all.where((v) => v['profile'] == 'relying_party')) {
      test('${v['name']}: ${v['accepted'] == true ? 'accepted' : 'refused'} (${v['reason']})',
          () {
        expect(isRelyingPartyText(v['text'], v['max'] as int), v['accepted'],
            reason: 'text ${jsonEncode(v['text'])}');
      });
    }
  });

  group('label profile', () {
    for (final v in all.where((v) => v['profile'] == 'label')) {
      test('${v['name']}: ${v['accepted'] == true ? 'accepted' : 'refused'} (${v['reason']})',
          () {
        // The label rules of T-245 have a fixed limit of 40 code points, which is what every label vector uses.
        expect(v['max'], labelMaxLength);
        expect(labelProblem(v['text'] as String) == null, v['accepted'],
            reason: 'text ${jsonEncode(v['text'])}');
      });
    }
  });

  test('a value that is not a string is refused, never thrown on', () {
    expect(isRelyingPartyText(null, 10), isFalse);
    expect(isRelyingPartyText(42, 10), isFalse);
    expect(isRelyingPartyText(<String>['a'], 10), isFalse);
  });

  test('a right-to-left override hidden in a payee is refused, not stripped', () {
    expect(isRelyingPartyText('Muster \u202Eeinegrenhcs GmbH', 70), isFalse);
    expect(isRelyingPartyText('Muster Energie GmbH', 70), isTrue);
  });
}
