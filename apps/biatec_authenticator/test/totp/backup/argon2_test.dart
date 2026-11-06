import 'dart:typed_data';

import 'package:biatec_authenticator/totp/backup/argon2.dart';
import 'package:flutter_test/flutter_test.dart';

Uint8List filled(int n, int v) => Uint8List.fromList(List.filled(n, v));

String hex(List<int> b) =>
    b.map((x) => x.toRadixString(16).padLeft(2, '0')).join();

void main() {
  test('RFC 9106 section 5.3: the Argon2id test vector', () {
    final tag = argon2id(
      filled(32, 0x01),
      filled(16, 0x02),
      const Argon2idParams(memoryKiB: 32, iterations: 3, lanes: 4),
      secret: filled(8, 0x03),
      additional: filled(12, 0x04),
    );
    expect(hex(tag),
        '0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659');
  });

  test('timing at the floor (for information)', () {
    final sw = Stopwatch()..start();
    argon2id(filled(12, 0x41), filled(16, 0x09),
        const Argon2idParams(memoryKiB: 65536, iterations: 3, lanes: 4));
    // ignore: avoid_print
    print('ARGON2 64MiB t=3 p=4: ${sw.elapsedMilliseconds} ms');
  });
}
