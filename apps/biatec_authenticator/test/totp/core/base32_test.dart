import 'dart:convert';
import 'dart:typed_data';

import 'package:biatec_authenticator/totp/core/base32.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Base32 (RFC 4648)', () {
    test('test vectors', () {
      const vectors = {
        'f': 'MY',
        'fo': 'MZXQ',
        'foo': 'MZXW6',
        'foob': 'MZXW6YQ',
        'fooba': 'MZXW6YTB',
        'foobar': 'MZXW6YTBOI',
      };
      vectors.forEach((plain, encoded) {
        expect(Base32.encode(utf8.encode(plain)), encoded);
        expect(utf8.decode(Base32.decode(encoded)), plain);
      });
    });

    test('tolerates case, spaces, dashes and padding as people paste them', () {
      final want = utf8.encode('foobar');
      expect(Base32.decode('mzxw 6ytb oi'), want);
      expect(Base32.decode('MZXW-6YTB-OI'), want);
      expect(Base32.decode('MZXW6YTBOI======'), want);
      expect(Base32.decode('  MZXW6YQ=  '), utf8.encode('foob'));
    });

    test('refuses anything else, without echoing the input', () {
      for (final bad in [
        '',
        '   ',
        '=',
        'MZXW1',
        'MZXW6YT!',
        'MZ=XW',
        'M',
        'MZX=====',
      ]) {
        try {
          Base32.decode(bad);
          fail('should refuse "$bad"');
        } on Base32Exception catch (e) {
          if (bad.length > 3) expect(e.message, isNot(contains(bad)));
        }
      }
    });

    test('leftover bits in the last character are dropped, as other apps do',
        () {
      // "MY" is the canonical "f"; "MZ" has other unused bits and gives the same byte.
      expect(Base32.decode('MZ'), utf8.encode('f'));
      expect(Base32.decode('MY'), utf8.encode('f'));
    });

    test('lengths that leave a partial character are accepted the same way',
        () {
      // 3 and 6 characters (and 27 or 33, as some services hand out) drop their last bits like other apps do.
      expect(Base32.decode('MZX'), utf8.encode('f'));
      expect(Base32.decode('MZXW6Y'), utf8.encode('foo'));
      final key27 = 'JBSWY3DPEHPK3PXPJBSWY3DPEHP';
      expect(Base32.decode(key27).length, 16);
    });

    test('round trip for every length', () {
      for (var n = 1; n < 70; n++) {
        final bytes =
            Uint8List.fromList(List.generate(n, (i) => (i * 37 + n) & 0xFF));
        expect(Base32.decode(Base32.encode(bytes)), bytes, reason: 'length $n');
      }
    });
  });
}
