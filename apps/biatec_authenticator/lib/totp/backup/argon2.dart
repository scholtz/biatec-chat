import 'dart:typed_data';

import 'package:pointycastle/export.dart';

/// Argon2id parameters (RFC 9106). Memory is in KiB, as in the RFC.
class Argon2idParams {
  const Argon2idParams(
      {required this.memoryKiB, required this.iterations, required this.lanes});

  final int memoryKiB;
  final int iterations;
  final int lanes;

  @override
  bool operator ==(Object other) =>
      other is Argon2idParams &&
      other.memoryKiB == memoryKiB &&
      other.iterations == iterations &&
      other.lanes == lanes;

  @override
  int get hashCode => Object.hash(memoryKiB, iterations, lanes);

  @override
  String toString() => 'Argon2id(m=${memoryKiB}KiB, t=$iterations, p=$lanes)';
}

/// Argon2id (version 0x13) from the maintained `pointycastle` package; nothing is implemented here. [secret] and [additional] are the RFC's optional inputs (the backup does not use them; the RFC test vector does).
///
/// Slow by design: run it off the UI thread (see `Isolate.run` in the backup service).
Uint8List argon2id(Uint8List password, Uint8List salt, Argon2idParams params,
    {int length = 32, Uint8List? secret, Uint8List? additional}) {
  final generator = Argon2BytesGenerator()
    ..init(Argon2Parameters(
      Argon2Parameters.ARGON2_id,
      salt,
      desiredKeyLength: length,
      secret: secret,
      additional: additional,
      iterations: params.iterations,
      memory: params.memoryKiB,
      lanes: params.lanes,
      version: Argon2Parameters.ARGON2_VERSION_13,
    ));
  final out = Uint8List(length);
  generator.deriveKey(password, 0, out, 0);
  return out;
}
