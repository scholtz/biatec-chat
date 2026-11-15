// T-247: the strict parser of the link code `biatec-authenticator://link?iss=<issuer>&id=<link_id>&s=<link_secret>`.
import 'dart:convert';
import 'dart:io';

import 'package:biatec_authenticator/links/link_code.dart';
import 'package:biatec_authenticator/links/link_services.dart';
import 'package:biatec_authenticator/text/rp_strings.dart';
import 'package:flutter_test/flutter_test.dart';

const _origin = 'https://oidc.biatec.test';
const _id = 'lnk_6f24862262c182126431e3a6';
const _secret = 'c6cWUY11Jq3nvnZ0yLm821tPt4OJSKXr_j070m1UOL4';

String _uri({
  String scheme = 'biatec-authenticator',
  String host = 'link',
  String iss = 'https%3A%2F%2Foidc.biatec.test',
  String id = _id,
  String s = _secret,
  String extra = '',
}) =>
    '$scheme://$host?iss=$iss&id=$id&s=$s$extra';

LinkCodeProblem? _problem(String raw,
    {String origin = _origin, Set<String> extra = const {}}) {
  try {
    LinkCode.parse(raw, enrolledOrigin: origin, extraIssuers: extra);
    return null;
  } on LinkCodeException catch (e) {
    return e.problem;
  }
}

void main() {
  test('the link code of every vector of vectors/link.json is accepted', () {
    final vectors = (jsonDecode(File(
                    '../../packages/consent-protocol/vectors/link.json')
                .readAsStringSync()) as Map)['vectors'] as List;
    expect(vectors, isNotEmpty);
    for (final v in vectors) {
      final created = ((v as Map)['link_created'] as Map).cast<String, Object?>();
      final code =
          LinkCode.parse(created['link_uri'] as String, enrolledOrigin: _origin);
      expect(code.issuer, _origin);
      expect(code.linkId, created['link_id']);
      expect(code.secret, hasLength(43));
    }
  });

  group('a valid code', () {
    test('gives the three values', () {
      final code = LinkCode.parse(_uri(), enrolledOrigin: _origin);
      expect(code.issuer, _origin);
      expect(code.linkId, _id);
      expect(code.secret, _secret);
    });

    test('a scheme in capitals is the same scheme', () {
      expect(_problem(_uri(scheme: 'BIATEC-AUTHENTICATOR')), isNull);
    });

    test('the origin of a URL with a path and a default port', () {
      expect(originOf('http://10.0.2.2:5080/mock-oidc'), 'http://10.0.2.2:5080');
      expect(originOf('https://oidc.biatec.io:443/x'), 'https://oidc.biatec.io');
      expect(originOf('HTTPS://OIDC.biatec.io'), 'https://oidc.biatec.io');
      expect(originOf('not a url'), '');
      expect(originOf('ftp://oidc.biatec.io'), '');
    });

    test('the development mock issuer is accepted only when it is allowed', () {
      final raw = _uri();
      expect(_problem(raw, origin: 'http://10.0.2.2:5080'),
          LinkCodeProblem.differentService);
      expect(
          _problem(raw,
              origin: 'http://10.0.2.2:5080',
              extra: {LinkServices.mockIssuer}),
          isNull);
    });
  });

  group('refused as not a link code', () {
    final cases = <String, String>{
      'another scheme': _uri(scheme: 'https'),
      'another custom scheme': _uri(scheme: 'otpauth'),
      'another host': _uri(host: 'unlink'),
      'a path': 'biatec-authenticator://link/x?iss=https%3A%2F%2Foidc.biatec.test&id=$_id&s=$_secret',
      'a port': 'biatec-authenticator://link:1?iss=https%3A%2F%2Foidc.biatec.test&id=$_id&s=$_secret',
      'a user name': 'biatec-authenticator://u@link?iss=https%3A%2F%2Foidc.biatec.test&id=$_id&s=$_secret',
      'an extra parameter': _uri(extra: '&x=1'),
      'an extra parameter before': 'biatec-authenticator://link?x=1&iss=https%3A%2F%2Foidc.biatec.test&id=$_id&s=$_secret',
      'a repeated parameter': _uri(extra: '&id=$_id'),
      'a missing iss': 'biatec-authenticator://link?id=$_id&s=$_secret',
      'a missing id': 'biatec-authenticator://link?iss=https%3A%2F%2Foidc.biatec.test&s=$_secret',
      'a missing s': 'biatec-authenticator://link?iss=https%3A%2F%2Foidc.biatec.test&id=$_id',
      'no query': 'biatec-authenticator://link',
      'an empty query': 'biatec-authenticator://link?',
      'an empty value': _uri(s: ''),
      'a parameter without a value': 'biatec-authenticator://link?iss=https%3A%2F%2Foidc.biatec.test&id&s=$_secret',
      'a fragment': '${_uri()}#x',
      'a secret that is too short': _uri(s: 'abc'),
      'a secret of the wrong alphabet': _uri(s: '${_secret.substring(0, 42)}+'),
      'a link id that is too short': _uri(id: 'lnk_1'),
      'a link id with a path in it': _uri(id: 'lnk_6f24862262c182126431e3a6%2F..'),
      'a bad percent escape': _uri(iss: 'https%3A%2F%2Foidc%ZZ'),
      'white space': '${_uri()} ',
      'a line break': '${_uri()}\n',
      'nothing': '',
      'plain text': 'hello',
      'an account code of another kind': 'otpauth://totp/x?secret=ABC',
    };
    cases.forEach((name, raw) {
      test(name, () {
        expect(_problem(raw), LinkCodeProblem.notALinkCode);
      });
    });
  });

  group('refused as for a different service', () {
    final cases = <String, String>{
      'another origin': _uri(iss: 'https%3A%2F%2Foidc.other.example'),
      'another port': _uri(iss: 'https%3A%2F%2Foidc.biatec.test%3A8443'),
      'plain http for an https server': _uri(iss: 'http%3A%2F%2Foidc.biatec.test'),
      'a trailing slash': _uri(iss: 'https%3A%2F%2Foidc.biatec.test%2F'),
      'a path': _uri(iss: 'https%3A%2F%2Foidc.biatec.test%2Fx'),
      'a look-alike host': _uri(iss: 'https%3A%2F%2Foidc.biatec.test.evil.example'),
      'a user name in the issuer': _uri(iss: 'https%3A%2F%2Foidc.biatec.test%40evil.example'),
      'the issuer not percent-encoded and different': _uri(iss: 'https://evil.example'),
      'a different case of the host': _uri(iss: 'https%3A%2F%2FOIDC.biatec.test'),
    };
    cases.forEach((name, raw) {
      test(name, () {
        expect(_problem(raw), LinkCodeProblem.differentService);
      });
    });

    test('the right message for each problem, in both languages', () {
      expect(RpStrings.en.t('differentService'),
          'This code is for a different authenticator service');
      expect(RpStrings.en.t('notALinkCode'), isNotEmpty);
      expect(RpStrings.sk.t('differentService'), isNot(RpStrings.en.t('differentService')));
    });

    test('the secret is not checked against anything before the issuer', () {
      // A code for another service is refused whatever else is in it, so its secret is never sent anywhere.
      expect(_problem(_uri(iss: 'https%3A%2F%2Fevil.example')),
          LinkCodeProblem.differentService);
    });
  });

  group('deep link routes', () {
    test('the full URI and the path form of the route are both read', () {
      final full = _uri();
      expect(LinkIntake.linkTextFromRoute(full), full);
      final query = full.substring(full.indexOf('?'));
      expect(LinkIntake.linkTextFromRoute('/link$query'), full);
      expect(LinkIntake.linkTextFromRoute('link$query'), full);
    });

    test('any other route is not a link code', () {
      expect(LinkIntake.linkTextFromRoute('/'), isNull);
      expect(LinkIntake.linkTextFromRoute('/approval/ciba_1'), isNull);
      expect(LinkIntake.linkTextFromRoute('/linked?x=1'), isNull);
      expect(LinkIntake.linkTextFromRoute('https://evil.example/link?x=1'),
          isNull);
    });

    test('the intake holds a code until it is taken, once', () {
      final intake = LinkIntake();
      var heard = 0;
      intake.addListener(() => heard++);
      expect(intake.hasPending, isFalse);
      intake.offer('a');
      intake.offer('b');
      expect(heard, 2);
      expect(intake.hasPending, isTrue);
      expect(intake.take(), 'b');
      expect(intake.take(), isNull);
    });
  });
}
